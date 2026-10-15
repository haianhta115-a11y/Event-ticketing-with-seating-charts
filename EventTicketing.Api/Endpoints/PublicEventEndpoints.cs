using System.Text;
using System.Text.Json;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Events;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Npgsql;
using EventTicketing.Api.Configuration;

namespace EventTicketing.Api.Endpoints;

public static class PublicEventEndpoints
{
    public static IEndpointRouteBuilder MapPublicEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api")
                       .WithTags("Public Events")
                       .AllowAnonymous();

        group.MapGet("/events/open", async (
            int? limit,
            string? cursor,
            AppDbContext db,
            IConnectionMultiplexer redis,
            RedisKeyScope scope,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var take = limit ?? 20;
            if (take <= 0) take = 20;
            if (take > 50) take = 50;

            var now = timeProvider.GetUtcNow().UtcDateTime;

            // Transactional audit revision makes publish/close effective immediately;
            // database scope prevents Redis cache leakage across isolated tenants/fixtures.
            long revision;
            try { revision = await db.SalesTransitions.MaxAsync(a => (long?)a.Id, ct) ?? 0; }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return Results.Json(new { errorCode = "SALES_SCHEMA_NOT_READY", message = "Schema mở bán chưa sẵn sàng." }, statusCode: 503);
            }
            var cacheKey = scope.Key($"events:open:v3:{db.Database.GetDbConnection().Database}:revision={revision}:limit={take}:cursor={cursor ?? "none"}");
            IDatabase? redisDb = null;
            try
            {
                redisDb = redis.GetDatabase();
                var cachedStr = await redisDb.StringGetAsync(cacheKey);
                if (cachedStr.HasValue)
                {
                    var cachedResponse = JsonSerializer.Deserialize<PublicEventListResponse>(
                        cachedStr.ToString(),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    // A 30-second cache must never keep an already-started nearest showtime
                    // visible. Requery to find the next eligible showtime/page when time passes.
                    if (cachedResponse != null && cachedResponse.Items.All(e => e.NearestShowtime.StartTime > now))
                        return Results.Ok(cachedResponse);
                }
            }
            catch
            {
                // Bỏ qua lỗi Redis
            }

            long cursorTicks = 0;
            int cursorEventId = 0;

            if (!string.IsNullOrEmpty(cursor))
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                    var parts = decoded.Split('_');
                    if (parts.Length == 2 && long.TryParse(parts[0], out var parsedTicks)
                        && parsedTicks > 0 && parsedTicks <= DateTime.MaxValue.Ticks
                        && int.TryParse(parts[1], out var parsedId) && parsedId > 0)
                    {
                        cursorTicks = parsedTicks;
                        cursorEventId = parsedId;
                    }
                }
                catch
                {
                    // Con trỏ không hợp lệ, tải trang đầu tiên
                }
            }

            var query = db.Events
                .AsNoTracking()
                .Where(e => e.Status == EventStatus.Published)
                .Select(e => new
                {
                    Event = e,
                    ValidShowtimes = e.Showtimes.Where(s => s.Status == ShowtimeStatus.OnSale && s.StartTime > now)
                })
                .Where(e => e.ValidShowtimes.Any())
                .Select(e => new
                {
                    e.Event.Id,
                    e.Event.Name,
                    e.Event.Location,
                    e.Event.ImageUrl,
                    ShowtimeCount = e.ValidShowtimes.Count(),
                    NearestShowtime = e.ValidShowtimes.OrderBy(s => s.StartTime).FirstOrDefault()
                })
                .Where(e => e.NearestShowtime != null);

            if (cursorTicks > 0)
            {
                var cursorTime = new DateTime(cursorTicks, DateTimeKind.Utc);
                query = query.Where(e => e.NearestShowtime!.StartTime > cursorTime ||
                                         (e.NearestShowtime.StartTime == cursorTime && e.Id > cursorEventId));
            }

            var items = await query
                .OrderBy(e => e.NearestShowtime!.StartTime)
                .ThenBy(e => e.Id)
                .Take(take + 1)
                .ToListAsync(ct);

            string? nextCursor = null;
            if (items.Count > take)
            {
                var nextItem = items[take - 1]; // Lấy phần tử cuối cùng của trang hiện tại
                var ticks = nextItem.NearestShowtime!.StartTime.Ticks;
                nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ticks}_{nextItem.Id}"));
                items.RemoveAt(take);
            }

            var eventIds = items.Select(e => e.Id).ToList();
            var validShowtimes = eventIds.Count > 0
                ? await db.Showtimes
                    .AsNoTracking()
                    .Where(s => eventIds.Contains(s.EventId) && s.Status == ShowtimeStatus.OnSale && s.StartTime > now)
                    .Select(s => new { s.Id, s.EventId })
                    .ToListAsync(ct)
                : [];

            var validShowtimeIds = validShowtimes.Select(s => s.Id).ToList();

            var categoryPrices = validShowtimeIds.Count > 0
                ? await db.SeatCategories
                    .AsNoTracking()
                    .Where(c => validShowtimeIds.Contains(c.ShowtimeId) && c.Price != null)
                    .Select(c => new { c.ShowtimeId, Price = c.Price!.Value })
                    .ToListAsync(ct)
                : [];

            var showtimeToEvent = validShowtimes.ToDictionary(s => s.Id, s => s.EventId);
            var pricesByEvent = categoryPrices
                .GroupBy(c => showtimeToEvent[c.ShowtimeId])
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        MinPrice = (decimal?)g.Min(x => x.Price),
                        MaxPrice = (decimal?)g.Max(x => x.Price)
                    });

            var responseItems = items.Select(e =>
            {
                pricesByEvent.TryGetValue(e.Id, out var pricing);
                return new PublicEventItemDto
                {
                    EventId = e.Id,
                    Name = e.Name,
                    Location = e.Location,
                    ImageUrl = e.ImageUrl,
                    ShowtimeCount = e.ShowtimeCount,
                    NearestShowtime = new PublicNearestShowtimeDto
                    {
                        Id = e.NearestShowtime!.Id,
                        StartTime = e.NearestShowtime.StartTime
                    },
                    MinPrice = pricing?.MinPrice,
                    MaxPrice = pricing?.MaxPrice
                };
            }).ToList();

            var response = new PublicEventListResponse
            {
                Items = responseItems,
                NextCursor = nextCursor
            };

            if (redisDb != null && responseItems.Count > 0)
            {
                try
                {
                    var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    await redisDb.StringSetAsync(cacheKey, json, TimeSpan.FromSeconds(30));
                }
                catch
                {
                    // Bỏ qua lỗi Redis
                }
            }

            return Results.Ok(response);
        })
        .WithName("GetOpenEvents")
        .WithSummary("Lấy danh sách sự kiện đang mở bán")
        .Produces<PublicEventListResponse>(StatusCodes.Status200OK);

        group.MapGet("/showtimes/{id}", async (
            int id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var showtime = await db.Showtimes
                .AsNoTracking()
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == id, ct);

            if (showtime == null || showtime.Status != ShowtimeStatus.OnSale || showtime.Event.Status != EventStatus.Published)
            {
                return Results.Ok(new { id = id, isOnSale = false });
            }

            var prices = await db.SeatCategories
                .AsNoTracking()
                .Where(c => c.ShowtimeId == id && c.Price != null)
                .Select(c => c.Price!.Value)
                .ToListAsync(ct);

            var dto = new PublicShowtimeDetailDto
            {
                Id = showtime.Id,
                IsOnSale = true,
                EventId = showtime.Event.Id,
                EventName = showtime.Event.Name,
                Description = showtime.Event.Description,
                Location = showtime.Event.Location,
                ImageUrl = showtime.Event.ImageUrl,
                StartTime = showtime.StartTime,
                EndTime = showtime.EndTime,
                MinPrice = prices.Count > 0 ? prices.Min() : null,
                MaxPrice = prices.Count > 0 ? prices.Max() : null
            };

            return Results.Ok(dto);
        })
        .WithName("GetPublicShowtimeDetail")
        .WithSummary("Lấy chi tiết suất diễn công khai")
        .Produces<PublicShowtimeDetailDto>(StatusCodes.Status200OK);

        return app;
    }
}
