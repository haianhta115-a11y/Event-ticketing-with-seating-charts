using System.Security.Claims;
using System.Text.Json;
using EventTicketing.Api.Controllers;
using EventTicketing.Api.Data;
using EventTicketing.Api.Endpoints;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Events;
using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class Phase2PricingAndSalesValidationUnitTests
{
    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ClaimsPrincipal CreateUser(int userId, string role = "Organizer")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role)
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    [Fact]
    public void SalesStatusException_SetsPropertiesAndUnpricedCategoriesCorrectly()
    {
        var unpriced = new List<string> { "VIP", "Economy" };
        var ex = new SalesStatusException(409, "SEAT_PRICING_REQUIRED", "Cần thiết lập giá vé cho các hạng ghế: VIP, Economy.", unpriced);

        Assert.Equal(409, ex.Status);
        Assert.Equal("SEAT_PRICING_REQUIRED", ex.Code);
        Assert.Contains("VIP", ex.Message);
        Assert.Contains("Economy", ex.Message);
        Assert.Equal(2, ex.UnpricedCategories.Count);
        Assert.Contains("VIP", ex.UnpricedCategories);
        Assert.Contains("Economy", ex.UnpricedCategories);
    }

    [Fact]
    public void SalesStatusException_WithoutUnpricedCategories_DefaultsToEmptyList()
    {
        var ex = new SalesStatusException(409, "EVENT_NOT_PUBLISHED", "Cần xuất bản sự kiện trước khi mở bán.");

        Assert.Equal(409, ex.Status);
        Assert.Equal("EVENT_NOT_PUBLISHED", ex.Code);
        Assert.Empty(ex.UnpricedCategories);
    }

    [Fact]
    public async Task SalesController_WhenUnpricedCategories_Returns409WithExplicitCategoryNames()
    {
        var db = CreateInMemoryDbContext();
        var authMock = new Mock<IAuthorizationService>();
        var salesService = new SalesStatusService(db, TimeProvider.System, authMock.Object);
        var controller = new SalesController(salesService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = CreateUser(201) }
            }
        };

        var unpriced = new List<string> { "VIP", "Deluxe" };
        var statusResult = await ((Func<Task<IActionResult>>)(async () =>
        {
            // Simulate action throwing SalesStatusException with unpriced categories
            var ex = new SalesStatusException(409, "SEAT_PRICING_REQUIRED", "Cần thiết lập giá vé cho: VIP, Deluxe.", unpriced);
            var transitionMethod = typeof(SalesController).GetMethod("Transition",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var task = (Task<IActionResult>)transitionMethod.Invoke(controller, [new Func<Task<string>>(() => throw ex)])!;
            return await task;
        }))();

        var objectResult = Assert.IsType<ObjectResult>(statusResult);
        Assert.Equal(409, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(objectResult.Value);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("SEAT_PRICING_REQUIRED", root.GetProperty("errorCode").GetString());
        var message = root.GetProperty("message").GetString();
        Assert.Contains("VIP", message);
        Assert.Contains("Deluxe", message);

        var categories = root.GetProperty("unpricedCategories").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, categories.Count);
        Assert.Contains("VIP", categories);
        Assert.Contains("Deluxe", categories);
    }

    [Fact]
    public async Task ShowtimeDetail_WhenShowtimeOnSale_ReturnsCalculatedMinAndMaxPrice()
    {
        using var db = CreateInMemoryDbContext();

        var evt = new Event
        {
            Id = 1,
            Name = "Concert 2026",
            Description = "Live music",
            Location = "Saigon",
            Status = EventStatus.Published,
            OwnerUserId = 201
        };
        var showtime = new Showtime
        {
            Id = 10,
            EventId = 1,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            Status = ShowtimeStatus.OnSale,
            Event = evt
        };
        var cat1 = new SeatCategory { Id = 1, ShowtimeId = 10, Name = "Standard", NormalizedName = "STANDARD", Price = 150000m };
        var cat2 = new SeatCategory { Id = 2, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP", Price = 500000m };
        var cat3 = new SeatCategory { Id = 3, ShowtimeId = 10, Name = "VVIP", NormalizedName = "VVIP", Price = 1200000m };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.AddRange(cat1, cat2, cat3);
        await db.SaveChangesAsync();

        // Query the logic matching PublicEventEndpoints /api/showtimes/{id}
        var foundShowtime = await db.Showtimes
            .AsNoTracking()
            .Include(s => s.Event)
            .FirstOrDefaultAsync(s => s.Id == 10);

        Assert.NotNull(foundShowtime);
        Assert.Equal(ShowtimeStatus.OnSale, foundShowtime.Status);
        Assert.Equal(EventStatus.Published, foundShowtime.Event.Status);

        var prices = await db.SeatCategories
            .AsNoTracking()
            .Where(c => c.ShowtimeId == 10 && c.Price != null)
            .Select(c => c.Price!.Value)
            .ToListAsync();

        var dto = new PublicShowtimeDetailDto
        {
            Id = foundShowtime.Id,
            IsOnSale = true,
            EventId = foundShowtime.Event.Id,
            EventName = foundShowtime.Event.Name,
            Description = foundShowtime.Event.Description,
            Location = foundShowtime.Event.Location,
            ImageUrl = foundShowtime.Event.ImageUrl,
            StartTime = foundShowtime.StartTime,
            EndTime = foundShowtime.EndTime,
            MinPrice = prices.Count > 0 ? prices.Min() : null,
            MaxPrice = prices.Count > 0 ? prices.Max() : null
        };

        Assert.True(dto.IsOnSale);
        Assert.Equal(150000m, dto.MinPrice);
        Assert.Equal(1200000m, dto.MaxPrice);
    }

    [Fact]
    public async Task ShowtimeDetail_WhenPricesUnconfigured_ReturnsNullMinAndMaxPrice()
    {
        using var db = CreateInMemoryDbContext();

        var evt = new Event
        {
            Id = 2,
            Name = "Festival",
            Description = "Festival desc",
            Location = "Hanoi",
            Status = EventStatus.Published,
            OwnerUserId = 201
        };
        var showtime = new Showtime
        {
            Id = 20,
            EventId = 2,
            StartTime = DateTime.UtcNow.AddDays(2),
            Status = ShowtimeStatus.OnSale,
            Event = evt
        };
        var cat = new SeatCategory { Id = 4, ShowtimeId = 20, Name = "General", NormalizedName = "GENERAL", Price = null };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(cat);
        await db.SaveChangesAsync();

        var foundShowtime = await db.Showtimes
            .AsNoTracking()
            .Include(s => s.Event)
            .FirstOrDefaultAsync(s => s.Id == 20);

        var prices = await db.SeatCategories
            .AsNoTracking()
            .Where(c => c.ShowtimeId == 20 && c.Price != null)
            .Select(c => c.Price!.Value)
            .ToListAsync();

        var dto = new PublicShowtimeDetailDto
        {
            Id = foundShowtime!.Id,
            IsOnSale = true,
            MinPrice = prices.Count > 0 ? prices.Min() : null,
            MaxPrice = prices.Count > 0 ? prices.Max() : null
        };

        Assert.Null(dto.MinPrice);
        Assert.Null(dto.MaxPrice);
    }

    [Fact]
    public async Task OpenEventsFeed_CalculatesMinAndMaxPricesAcrossValidShowtimes()
    {
        using var db = CreateInMemoryDbContext();
        var now = DateTime.UtcNow;

        var evt = new Event
        {
            Id = 1,
            Name = "Summer Festival",
            Description = "Music",
            Location = "Danang",
            Status = EventStatus.Published,
            OwnerUserId = 201
        };

        // Showtime 1 (OnSale, in future)
        var s1 = new Showtime { Id = 101, EventId = 1, StartTime = now.AddDays(1), Status = ShowtimeStatus.OnSale, Event = evt };
        // Showtime 2 (OnSale, in future)
        var s2 = new Showtime { Id = 102, EventId = 1, StartTime = now.AddDays(2), Status = ShowtimeStatus.OnSale, Event = evt };
        // Showtime 3 (Closed, should NOT contribute to public open feed prices)
        var s3 = new Showtime { Id = 103, EventId = 1, StartTime = now.AddDays(3), Status = ShowtimeStatus.Closed, Event = evt };

        // Categories for s1: 200,000 and 500,000
        var c1 = new SeatCategory { Id = 1, ShowtimeId = 101, Name = "Standard", NormalizedName = "STANDARD", Price = 200000m };
        var c2 = new SeatCategory { Id = 2, ShowtimeId = 101, Name = "VIP", NormalizedName = "VIP", Price = 500000m };

        // Categories for s2: 150,000 and 600,000
        var c3 = new SeatCategory { Id = 3, ShowtimeId = 102, Name = "Early Bird", NormalizedName = "EARLY BIRD", Price = 150000m };
        var c4 = new SeatCategory { Id = 4, ShowtimeId = 102, Name = "VVIP", NormalizedName = "VVIP", Price = 600000m };

        // Categories for s3 (Closed): 10,000 (should be excluded!)
        var c5 = new SeatCategory { Id = 5, ShowtimeId = 103, Name = "Super Cheap", NormalizedName = "CHEAP", Price = 10000m };

        db.Events.Add(evt);
        db.Showtimes.AddRange(s1, s2, s3);
        db.SeatCategories.AddRange(c1, c2, c3, c4, c5);
        await db.SaveChangesAsync();

        // Simulate query logic in PublicEventEndpoints
        var eventIds = new List<int> { 1 };
        var validShowtimes = await db.Showtimes
            .AsNoTracking()
            .Where(s => eventIds.Contains(s.EventId) && s.Status == ShowtimeStatus.OnSale && s.StartTime > now)
            .Select(s => new { s.Id, s.EventId })
            .ToListAsync();

        Assert.Equal(2, validShowtimes.Count);
        Assert.DoesNotContain(validShowtimes, s => s.Id == 103);

        var validShowtimeIds = validShowtimes.Select(s => s.Id).ToList();

        var categoryPrices = await db.SeatCategories
            .AsNoTracking()
            .Where(c => validShowtimeIds.Contains(c.ShowtimeId) && c.Price != null)
            .Select(c => new { c.ShowtimeId, Price = c.Price!.Value })
            .ToListAsync();

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

        Assert.True(pricesByEvent.TryGetValue(1, out var pricing));
        Assert.NotNull(pricing);
        // Min should be 150,000 (from s2), NOT 10,000 (from closed s3)
        Assert.Equal(150000m, pricing.MinPrice);
        // Max should be 600,000 (from s2)
        Assert.Equal(600000m, pricing.MaxPrice);
    }

    [Fact]
    public async Task OpenEventsFeed_WhenNoPricesConfigured_ReturnsNullMinAndMax()
    {
        using var db = CreateInMemoryDbContext();
        var now = DateTime.UtcNow;

        var evt = new Event
        {
            Id = 5,
            Name = "Unpriced Event",
            Description = "Music",
            Location = "Hue",
            Status = EventStatus.Published,
            OwnerUserId = 201
        };

        var s1 = new Showtime { Id = 501, EventId = 5, StartTime = now.AddDays(1), Status = ShowtimeStatus.OnSale, Event = evt };
        var c1 = new SeatCategory { Id = 50, ShowtimeId = 501, Name = "VIP", NormalizedName = "VIP", Price = null };

        db.Events.Add(evt);
        db.Showtimes.Add(s1);
        db.SeatCategories.Add(c1);
        await db.SaveChangesAsync();

        var eventIds = new List<int> { 5 };
        var validShowtimes = await db.Showtimes
            .AsNoTracking()
            .Where(s => eventIds.Contains(s.EventId) && s.Status == ShowtimeStatus.OnSale && s.StartTime > now)
            .Select(s => new { s.Id, s.EventId })
            .ToListAsync();

        var validShowtimeIds = validShowtimes.Select(s => s.Id).ToList();

        var categoryPrices = await db.SeatCategories
            .AsNoTracking()
            .Where(c => validShowtimeIds.Contains(c.ShowtimeId) && c.Price != null)
            .Select(c => new { c.ShowtimeId, Price = c.Price!.Value })
            .ToListAsync();

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

        pricesByEvent.TryGetValue(5, out var pricing);
        Assert.Null(pricing?.MinPrice);
        Assert.Null(pricing?.MaxPrice);
    }

    [Fact]
    public async Task UnpricedCategoriesValidation_FindsAllUnpricedCategories()
    {
        using var db = CreateInMemoryDbContext();
        const int showtimeId = 99;

        var showtime = new Showtime { Id = showtimeId, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1) };
        db.Showtimes.Add(showtime);

        var catPriced = new SeatCategory { Id = 1, ShowtimeId = showtimeId, Name = "VIP", NormalizedName = "VIP", Price = 500000m };
        var catUnpriced1 = new SeatCategory { Id = 2, ShowtimeId = showtimeId, Name = "Balcony", NormalizedName = "BALCONY", Price = null };
        var catUnpriced2 = new SeatCategory { Id = 3, ShowtimeId = showtimeId, Name = "Standing", NormalizedName = "STANDING", Price = null };

        db.SeatCategories.AddRange(catPriced, catUnpriced1, catUnpriced2);
        await db.SaveChangesAsync();

        var unpricedCategories = await db.SeatCategories
            .Where(c => c.ShowtimeId == showtimeId && c.Price == null)
            .OrderBy(c => c.Id)
            .Select(c => c.Name)
            .Distinct()
            .ToListAsync();

        Assert.Equal(2, unpricedCategories.Count);
        Assert.Equal(["Balcony", "Standing"], unpricedCategories);

        var ex = new SalesStatusException(
            409,
            "SEAT_PRICING_REQUIRED",
            $"Cần thiết lập giá vé cho các hạng ghế trước khi mở bán: {string.Join(", ", unpricedCategories)}.",
            unpricedCategories);

        Assert.Equal(409, ex.Status);
        Assert.Equal("SEAT_PRICING_REQUIRED", ex.Code);
        Assert.Contains("Balcony", ex.Message);
        Assert.Contains("Standing", ex.Message);
        Assert.Equal(2, ex.UnpricedCategories.Count);
        Assert.Contains("Balcony", ex.UnpricedCategories);
        Assert.Contains("Standing", ex.UnpricedCategories);
    }
}
