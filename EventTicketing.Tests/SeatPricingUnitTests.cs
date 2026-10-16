using System.Security.Claims;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Pricing;
using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class SeatPricingUnitTests
{
    private static (AppDbContext Db, Mock<IAuthorizationService> Auth, SeatPricingService Service) CreateTestHarness()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        var authMock = new Mock<IAuthorizationService>();
        var timeProvider = TimeProvider.System;

        var service = new SeatPricingService(db, authMock.Object, timeProvider);
        return (db, authMock, service);
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

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    [InlineData(-100000)]
    public async Task UpdateEventPricing_NegativePrice_ThrowsSeatPricingException_WithVietnameseMessage(decimal negativePrice)
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = negativePrice }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_PRICE", ex.Code);
        Assert.Equal("Giá vé phải lớn hơn hoặc bằng 0.", ex.Message);
    }

    [Fact]
    public async Task UpdateEventPricing_EmptyList_ThrowsSeatPricingException_WithVietnameseMessage()
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest { Categories = new() };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_PRICING_REQUEST", ex.Code);
        Assert.Equal("Danh sách giá vé không được để trống.", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UpdateEventPricing_InvalidCategoryId_ThrowsSeatPricingException(int invalidId)
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = invalidId, Price = 100000 }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_CATEGORY_ID", ex.Code);
        Assert.Equal("Mã hạng ghế không hợp lệ.", ex.Message);
    }

    [Fact]
    public async Task UpdateEventPricing_NonExistentEvent_ThrowsNotFound()
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = 150000 }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(999, request, user));

        Assert.Equal(404, ex.Status);
        Assert.Equal("EVENT_NOT_FOUND", ex.Code);
        Assert.Equal("Không tìm thấy sự kiện.", ex.Message);
    }

    [Fact]
    public async Task UpdateEventPricing_ForbiddenUser_ThrowsForbidden()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(999);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, evt, AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Failed());

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = 150000 }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(403, ex.Status);
        Assert.Equal("EVENT_FORBIDDEN", ex.Code);
        Assert.Equal("Bạn không có quyền quản lý sự kiện này.", ex.Message);
    }

    [Fact]
    public async Task UpdateEventPricing_EndedEvent_ThrowsConflict()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201, Status = EventStatus.Ended };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, evt, AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = 150000 }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(409, ex.Status);
        Assert.Equal("EVENT_ENDED", ex.Code);
        Assert.Equal("Không thể thay đổi giá vé cho sự kiện đã kết thúc.", ex.Message);
    }

    [Fact]
    public async Task UpdateEventPricing_CategoryNotFoundInEvent_ThrowsNotFound()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        var showtime = new Showtime { Id = 10, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1) };
        var category = new SeatCategory { Id = 5, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP", Price = 100000 };
        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(category);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, evt, AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 999, Price = 200000 }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(404, ex.Status);
        Assert.Equal("CATEGORY_NOT_FOUND", ex.Code);
        Assert.Contains("Không tìm thấy hạng ghế với mã 999 trong sự kiện này.", ex.Message);
    }

    [Fact]
    public async Task UpdateEventPricing_AuthorizedOwner_SuccessfullyUpdatesPrices()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        var showtime = new Showtime { Id = 10, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1) };
        var catVip = new SeatCategory { Id = 1, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP", Price = 0 };
        var catStandard = new SeatCategory { Id = 2, ShowtimeId = 10, Name = "Standard", NormalizedName = "STANDARD", Price = 0 };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.AddRange(catVip, catStandard);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, evt, AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = 500000m },
                new() { CategoryId = 2, Price = 250000m }
            }
        };

        var result = await service.UpdateEventSeatPricingAsync(1, request, user);

        Assert.Equal(2, result.Count);
        Assert.Equal(500000m, result.Single(c => c.Id == 1).Price);
        Assert.Equal(250000m, result.Single(c => c.Id == 2).Price);

        // Verify in database
        var updatedVip = await db.SeatCategories.FindAsync(1);
        var updatedStandard = await db.SeatCategories.FindAsync(2);
        Assert.Equal(500000m, updatedVip!.Price);
        Assert.Equal(250000m, updatedStandard!.Price);
    }

    [Fact]
    public async Task GetShowtimePricing_ReturnsAllCategoriesWithSeatCounts()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        var showtime = new Showtime { Id = 10, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1) };
        var catVip = new SeatCategory { Id = 1, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP", Price = 500000m };
        var seat1 = new Seat { Id = 101, ShowtimeId = 10, SeatCategoryId = 1, Row = "A", Number = "1" };
        var seat2 = new Seat { Id = 102, ShowtimeId = 10, SeatCategoryId = 1, Row = "A", Number = "2" };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(catVip);
        db.Seats.AddRange(seat1, seat2);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, It.IsAny<Event>(), AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var result = await service.GetShowtimeSeatPricingAsync(10, user);

        Assert.Single(result);
        var item = result[0];
        Assert.Equal(1, item.Id);
        Assert.Equal(10, item.ShowtimeId);
        Assert.Equal("VIP", item.Name);
        Assert.Equal(500000m, item.Price);
        Assert.Equal(2, item.SeatCount);
    }

    [Fact]
    public async Task UpdateShowtimePricing_ClosedShowtime_ThrowsConflict()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        var showtime = new Showtime { Id = 10, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1), Status = ShowtimeStatus.Closed };
        var cat = new SeatCategory { Id = 1, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP", Price = 100000m };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(cat);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, It.IsAny<Event>(), AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = 200000m }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateShowtimeSeatPricingAsync(10, request, user));

        Assert.Equal(409, ex.Status);
        Assert.Equal("SHOWTIME_CLOSED", ex.Code);
        Assert.Equal("Không thể thay đổi giá vé cho suất diễn đã đóng.", ex.Message);
    }

    [Fact]
    public async Task Dto_PropertyAliases_SupportAlternatePropertyNames()
    {
        var item = new SeatCategoryPricingItemDto { SeatCategoryId = 42, Price = 100000m };
        Assert.Equal(42, item.CategoryId);
        Assert.Equal(42, item.SeatCategoryId);

        var request = new UpdateSeatPricingRequest { Prices = [item] };
        Assert.Single(request.Categories);
        Assert.Equal(42, request.Categories[0].CategoryId);
    }

    [Fact]
    public async Task UpdateEventPricing_NullPrice_ThrowsSeatPricingException()
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = null }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_PRICE", ex.Code);
        Assert.Equal("Giá vé không được để trống.", ex.Message);
    }

    [Theory]
    [InlineData(100.5)]
    [InlineData(0.99)]
    [InlineData(250000.1)]
    [InlineData(0.0001)]
    public async Task UpdateEventPricing_FractionalPrice_ThrowsSeatPricingException(decimal fractionalPrice)
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = fractionalPrice }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_PRICE", ex.Code);
        Assert.Equal("Giá vé phải là số nguyên (không có phần thập phân).", ex.Message);
    }

    [Theory]
    [InlineData(10_000_000_000_000_000)] // 10^16, overflows numeric(18,2)
    [InlineData(99_999_999_999_999_999)]
    public async Task UpdateEventPricing_OverflowPrice_ThrowsSeatPricingException(decimal overflowPrice)
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var request = new UpdateSeatPricingRequest
        {
            Categories = new List<SeatCategoryPricingItemDto>
            {
                new() { CategoryId = 1, Price = overflowPrice }
            }
        };

        var ex = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateEventSeatPricingAsync(1, request, user));

        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_PRICE", ex.Code);
        Assert.Equal("Giá vé vượt quá giới hạn cho phép.", ex.Message);
    }

    [Fact]
    public async Task UpdateShowtimePricing_NullOrFractionalOrOverflow_ThrowsSeatPricingException()
    {
        var (db, _, service) = CreateTestHarness();
        var user = CreateUser(201);

        var nullRequest = new UpdateSeatPricingRequest
        {
            Categories = [new() { CategoryId = 1, Price = null }]
        };
        var exNull = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateShowtimeSeatPricingAsync(10, nullRequest, user));
        Assert.Equal(400, exNull.Status);
        Assert.Equal("INVALID_PRICE", exNull.Code);

        var fracRequest = new UpdateSeatPricingRequest
        {
            Categories = [new() { CategoryId = 1, Price = 50.5m }]
        };
        var exFrac = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateShowtimeSeatPricingAsync(10, fracRequest, user));
        Assert.Equal(400, exFrac.Status);
        Assert.Equal("INVALID_PRICE", exFrac.Code);

        var overRequest = new UpdateSeatPricingRequest
        {
            Categories = [new() { CategoryId = 1, Price = 10_000_000_000_000_000m }]
        };
        var exOver = await Assert.ThrowsAsync<SeatPricingException>(() =>
            service.UpdateShowtimeSeatPricingAsync(10, overRequest, user));
        Assert.Equal(400, exOver.Status);
        Assert.Equal("INVALID_PRICE", exOver.Code);
    }

    [Fact]
    public async Task GetEventPricing_UnsetPrice_ReturnsNullPrice()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        var showtime = new Showtime { Id = 10, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1) };
        var cat = new SeatCategory { Id = 1, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP" }; // Price unset (null)

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(cat);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, evt, AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var result = await service.GetEventSeatPricingAsync(1, user);

        Assert.Single(result);
        Assert.Null(result[0].Price);
    }

    [Fact]
    public async Task GetShowtimePricing_UnsetPrice_ReturnsNullPrice()
    {
        var (db, authMock, service) = CreateTestHarness();
        var user = CreateUser(201);

        var evt = new Event { Id = 1, Name = "Test Event", Description = "Desc", Location = "Loc", OwnerUserId = 201 };
        var showtime = new Showtime { Id = 10, EventId = 1, StartTime = DateTime.UtcNow.AddDays(1) };
        var cat = new SeatCategory { Id = 1, ShowtimeId = 10, Name = "VIP", NormalizedName = "VIP" }; // Price unset (null)

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(cat);
        await db.SaveChangesAsync();

        authMock.Setup(a => a.AuthorizeAsync(user, It.IsAny<Event>(), AccessPolicies.EventOwner))
            .ReturnsAsync(AuthorizationResult.Success());

        var result = await service.GetShowtimeSeatPricingAsync(10, user);

        Assert.Single(result);
        Assert.Null(result[0].Price);
    }
}
