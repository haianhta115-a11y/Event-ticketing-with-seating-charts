using System.Text;
using System.Text.Json;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Events;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

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
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var take = limit ?? 20;
            if (take <= 0) take = 20;
            if (take > 50) take = 50;

            var now = timeProvider.GetUtcNow().UtcDateTime;

            var cacheKey = $"events:open:limit={take}:cursor={cursor ?? "none"}";
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
                    if (cachedResponse != null)
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
                    if (parts.Length == 2)
                    {
                        long.TryParse(parts[0], out cursorTicks);
                        int.TryParse(parts[1], out cursorEventId);
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

            var responseItems = items.Select(e => new PublicEventItemDto
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
                MinPrice = null, // TODO: Cập nhật giá ở story S-15
                MaxPrice = null  // TODO: Cập nhật giá ở story S-15
            }).ToList();

            var response = new PublicEventListResponse
            {
                Items = responseItems,
                NextCursor = nextCursor
            };

            if (redisDb != null)
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
                MinPrice = null, // TODO: Cập nhật giá ở story S-15
                MaxPrice = null  // TODO: Cập nhật giá ở story S-15
            };

            return Results.Ok(dto);
        })
        .WithName("GetPublicShowtimeDetail")
        .WithSummary("Lấy chi tiết suất diễn công khai")
        .Produces<PublicShowtimeDetailDto>(StatusCodes.Status200OK);

        return app;
    }
}
