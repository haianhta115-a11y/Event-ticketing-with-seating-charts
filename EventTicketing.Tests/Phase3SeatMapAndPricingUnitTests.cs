using System.Security.Claims;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class Phase3SeatMapAndPricingUnitTests
{
    private static (AppDbContext Db, Mock<IReservationClock> ClockMock, SeatReservationService Service) CreateTestHarness()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var service = new SeatReservationService(db, clockMock.Object);
        return (db, clockMock, service);
    }

    [Fact]
    public void SeatDisplayDto_IncludesNullablePriceProperty()
    {
        var dto = new SeatDisplayDto(
            Id: 1,
            PerformanceId: 10,
            SeatCategoryId: 2,
            Row: "A",
            Number: "1",
            X: 10,
            Y: 20,
            DisplayStatus: "Available",
            HeldUntilUtc: null,
            CanSelect: true,
            Category: "VIP",
            Price: 500000m
        );

        Assert.Equal(500000m, dto.Price);
        Assert.Equal("VIP", dto.Category);

        var unpricedDto = new SeatDisplayDto(
            Id: 2,
            PerformanceId: 10,
            SeatCategoryId: 3,
            Row: "A",
            Number: "2",
            X: 30,
            Y: 20,
            DisplayStatus: "Available",
            HeldUntilUtc: null,
            CanSelect: true,
            Category: "Standard",
            Price: null
        );

        Assert.Null(unpricedDto.Price);
    }

    [Fact]
    public void HeldSeatDto_IncludesNullablePriceProperty()
    {
        var heldDto = new HeldSeatDto(
            Id: 1,
            Row: "A",
            Number: "1",
            SeatCategoryId: 2,
            HeldUntilUtc: DateTime.UtcNow.AddMinutes(10),
            Price: 750000m
        );

        Assert.Equal(750000m, heldDto.Price);

        var unpricedHeld = new HeldSeatDto(
            Id: 2,
            Row: "A",
            Number: "2",
            SeatCategoryId: 3,
            HeldUntilUtc: DateTime.UtcNow.AddMinutes(10)
        );

        Assert.Null(unpricedHeld.Price);
    }

    [Fact]
    public async Task GetSeatingChartStatusAsync_MapsCategoryPrice_IntoSeatDisplayDto()
    {
        var (db, _, service) = CreateTestHarness();

        var evt = new Event
        {
            Id = 1,
            Name = "Concert",
            Description = "Live",
            Location = "Hanoi",
            Status = EventStatus.Published,
            OwnerUserId = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Events.Add(evt);

        var showtime = new Showtime
        {
            Id = 10,
            EventId = 1,
            Event = evt,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            Status = ShowtimeStatus.OnSale
        };
        db.Showtimes.Add(showtime);

        var vipCategory = new SeatCategory
        {
            Id = 1,
            ShowtimeId = 10,
            Name = "VIP",
            NormalizedName = "vip",
            Price = 300000m
        };
        var standardCategory = new SeatCategory
        {
            Id = 2,
            ShowtimeId = 10,
            Name = "Standard",
            NormalizedName = "standard",
            Price = null // Unset price
        };
        db.SeatCategories.AddRange(vipCategory, standardCategory);

        var seatVip = new Seat
        {
            Id = 101,
            ShowtimeId = 10,
            SeatCategoryId = 1,
            SeatCategory = vipCategory,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Available,
            X = 10,
            Y = 10
        };
        var seatStandard = new Seat
        {
            Id = 102,
            ShowtimeId = 10,
            SeatCategoryId = 2,
            SeatCategory = standardCategory,
            Row = "B",
            Number = "1",
            Status = SeatStatus.Available,
            X = 10,
            Y = 30
        };
        db.Seats.AddRange(seatVip, seatStandard);
        await db.SaveChangesAsync();

        var seats = await service.GetSeatingChartStatusAsync(10);

        Assert.Equal(2, seats.Count);

        var vipResult = seats.First(s => s.Id == 101);
        Assert.Equal("VIP", vipResult.Category);
        Assert.Equal(300000m, vipResult.Price);

        var standardResult = seats.First(s => s.Id == 102);
        Assert.Equal("Standard", standardResult.Category);
        Assert.Null(standardResult.Price);
    }

    [Fact]
    public async Task GetUserHoldSessionAsync_MapsCategoryPrice_IntoHeldSeatDto()
    {
        var (db, _, service) = CreateTestHarness();

        var evt = new Event
        {
            Id = 2,
            Name = "Opera",
            Description = "Music",
            Location = "HCMC",
            Status = EventStatus.Published,
            OwnerUserId = 100,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Events.Add(evt);

        var showtime = new Showtime
        {
            Id = 20,
            EventId = 2,
            Event = evt,
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(2),
            Status = ShowtimeStatus.OnSale
        };
        db.Showtimes.Add(showtime);

        var category = new SeatCategory
        {
            Id = 5,
            ShowtimeId = 20,
            Name = "Deluxe",
            NormalizedName = "deluxe",
            Price = 450000m
        };
        db.SeatCategories.Add(category);

        var heldSeat = new Seat
        {
            Id = 201,
            ShowtimeId = 20,
            SeatCategoryId = 5,
            SeatCategory = category,
            Row = "C",
            Number = "5",
            Status = SeatStatus.Held,
            HeldByUserId = 999,
            HeldUntil = new DateTime(2026, 10, 16, 12, 10, 0, DateTimeKind.Utc),
            X = 20,
            Y = 20
        };
        db.Seats.Add(heldSeat);
        await db.SaveChangesAsync();

        var session = await service.GetUserHoldSessionAsync(20, 999);

        Assert.NotNull(session);
        Assert.Single(session.HeldSeats);
        Assert.Equal(450000m, session.HeldSeats[0].Price);
        Assert.Equal("C", session.HeldSeats[0].Row);
        Assert.Equal("5", session.HeldSeats[0].Number);
    }
}
