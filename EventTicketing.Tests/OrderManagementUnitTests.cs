using System.Security.Claims;
using EventTicketing.Api.Configuration;
using EventTicketing.Api.Controllers;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Orders;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class OrderManagementUnitTests
{
    private static (AppDbContext Db, Mock<IReservationClock> ClockMock, OrderService Service, DateTime FixedNow) CreateTestHarness(int paymentTimeoutMinutes = 15)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var orderOptions = Options.Create(new OrderOptions { PaymentTimeoutMinutes = paymentTimeoutMinutes });
        var service = new OrderService(db, clockMock.Object, orderOptions);
        return (db, clockMock, service, fixedTime);
    }

    [Fact]
    public void OrderCalculator_CalculateTotal_SumsAllItems()
    {
        var items = new List<OrderItem>
        {
            new() { UnitPrice = 150000m },
            new() { UnitPrice = 250000m },
            new() { UnitPrice = 500000m }
        };

        var total = OrderCalculator.CalculateTotal(items);

        Assert.Equal(900000m, total);
    }

    [Fact]
    public void OrderCalculator_CalculateTotal_EmptyList_ReturnsZero()
    {
        var total = OrderCalculator.CalculateTotal(Array.Empty<OrderItem>());
        Assert.Equal(0m, total);
    }

    [Fact]
    public void OrderCalculator_UnitPriceSnapshot_ImmunizesFromSubsequentPriceChanges()
    {
        var category = new SeatCategory
        {
            Id = 1,
            ShowtimeId = 1,
            Name = "VIP",
            NormalizedName = "VIP",
            Price = 500000m
        };

        var orderItem = new OrderItem
        {
            SeatId = 101,
            UnitPrice = category.Price.Value
        };

        // Price changes on category later
        category.Price = 800000m;

        // Order item's price is a snapshot and remains 500000m
        Assert.Equal(500000m, orderItem.UnitPrice);
        Assert.Equal(500000m, OrderCalculator.CalculateTotal([orderItem]));
    }

    [Fact]
    public async Task CreateOrderFromHolds_ValidHeldSeats_CreatesOrderAndExtendsHolds()
    {
        var (db, _, service, now) = CreateTestHarness(paymentTimeoutMinutes: 15);
        const int userId = 42;
        const int showtimeId = 10;

        var evt = new Event { Id = 1, Name = "Concert", Description = "Desc", Location = "Hall", Status = EventStatus.Published };
        var showtime = new Showtime { Id = showtimeId, EventId = 1, Event = evt, StartTime = now.AddHours(2), Status = ShowtimeStatus.OnSale };
        var category = new SeatCategory { Id = 1, ShowtimeId = showtimeId, Name = "Standard", NormalizedName = "STANDARD", Price = 200000m };

        var seat1 = new Seat
        {
            Id = 101,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Held,
            HeldByUserId = userId,
            HeldUntil = now.AddMinutes(5) // Still valid for 5 min
        };

        var seat2 = new Seat
        {
            Id = 102,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "2",
            Status = SeatStatus.Held,
            HeldByUserId = userId,
            HeldUntil = now.AddMinutes(5)
        };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(category);
        db.Seats.AddRange(seat1, seat2);
        await db.SaveChangesAsync();

        // Action
        var result = await service.CreateOrderFromHoldsAsync(showtimeId, userId);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.False(result.IsExistingPendingOrder);
        Assert.NotNull(result.Order);

        var order = result.Order;
        Assert.Equal(userId, order.UserId);
        Assert.Equal(showtimeId, order.ShowtimeId);
        Assert.Equal("Pending", order.Status);
        Assert.Equal(400000m, order.TotalAmount); // 200k * 2
        Assert.Equal(now.AddMinutes(15), order.ExpiresAtUtc);
        Assert.Equal(2, order.Items.Count);

        // Verify that seat holds in database were extended to payment timeout
        var reloadedSeat1 = await db.Seats.FindAsync(101);
        var reloadedSeat2 = await db.Seats.FindAsync(102);
        Assert.NotNull(reloadedSeat1);
        Assert.NotNull(reloadedSeat2);
        Assert.Equal(now.AddMinutes(15), reloadedSeat1.HeldUntil);
        Assert.Equal(now.AddMinutes(15), reloadedSeat2.HeldUntil);
    }

    [Fact]
    public async Task CreateOrderFromHolds_ExpiredSeat_Returns409AndIdentifiesLostSeats()
    {
        var (db, _, service, now) = CreateTestHarness();
        const int userId = 42;
        const int showtimeId = 10;

        var evt = new Event { Id = 1, Name = "Concert", Description = "Desc", Location = "Hall", Status = EventStatus.Published };
        var showtime = new Showtime { Id = showtimeId, EventId = 1, Event = evt, StartTime = now.AddHours(2), Status = ShowtimeStatus.OnSale };
        var category = new SeatCategory { Id = 1, ShowtimeId = showtimeId, Name = "Standard", NormalizedName = "STANDARD", Price = 200000m };

        var seat1 = new Seat
        {
            Id = 101,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Held,
            HeldByUserId = userId,
            HeldUntil = now.AddMinutes(5) // Valid
        };

        var seat2 = new Seat
        {
            Id = 102,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "2",
            Status = SeatStatus.Held,
            HeldByUserId = userId,
            HeldUntil = now.AddMinutes(-1) // Expired 1 min ago!
        };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(category);
        db.Seats.AddRange(seat1, seat2);
        await db.SaveChangesAsync();

        // Action
        var result = await service.CreateOrderFromHoldsAsync(showtimeId, userId);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(CreateOrderFailureReason.SeatHoldExpired, result.FailureReason);
        Assert.Single(result.LostSeats);
        Assert.Equal(102, result.LostSeats[0].SeatId);
        Assert.Equal("A", result.LostSeats[0].Row);
        Assert.Equal("2", result.LostSeats[0].Number);

        // Verify NO order was created in DB
        var orderCount = await db.Orders.CountAsync();
        Assert.Equal(0, orderCount);
    }

    [Fact]
    public async Task CreateOrderFromHolds_CalledTwice_ReturnsExistingPendingOrder()
    {
        var (db, _, service, now) = CreateTestHarness(paymentTimeoutMinutes: 15);
        const int userId = 42;
        const int showtimeId = 10;

        var evt = new Event { Id = 1, Name = "Concert", Description = "Desc", Location = "Hall", Status = EventStatus.Published };
        var showtime = new Showtime { Id = showtimeId, EventId = 1, Event = evt, StartTime = now.AddHours(2), Status = ShowtimeStatus.OnSale };
        var category = new SeatCategory { Id = 1, ShowtimeId = showtimeId, Name = "Standard", NormalizedName = "STANDARD", Price = 200000m };

        var seat = new Seat
        {
            Id = 101,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Held,
            HeldByUserId = userId,
            HeldUntil = now.AddMinutes(5)
        };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(category);
        db.Seats.Add(seat);
        await db.SaveChangesAsync();

        // First call
        var firstResult = await service.CreateOrderFromHoldsAsync(showtimeId, userId);
        Assert.True(firstResult.IsSuccess);
        Assert.False(firstResult.IsExistingPendingOrder);

        // Second call (double click / re-try)
        var secondResult = await service.CreateOrderFromHoldsAsync(showtimeId, userId);
        Assert.True(secondResult.IsSuccess);
        Assert.True(secondResult.IsExistingPendingOrder);
        Assert.Equal(firstResult.Order!.Id, secondResult.Order!.Id);

        // Verify only 1 order exists in database
        var totalOrders = await db.Orders.CountAsync();
        Assert.Equal(1, totalOrders);
    }

    [Fact]
    public async Task CreateOrderFromHolds_NoSeatsHeld_ReturnsFailure()
    {
        var (db, _, service, now) = CreateTestHarness();
        const int userId = 42;
        const int showtimeId = 10;

        var evt = new Event { Id = 1, Name = "Concert", Description = "Desc", Location = "Hall", Status = EventStatus.Published };
        var showtime = new Showtime { Id = showtimeId, EventId = 1, Event = evt, StartTime = now.AddHours(2), Status = ShowtimeStatus.OnSale };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        await db.SaveChangesAsync();

        var result = await service.CreateOrderFromHoldsAsync(showtimeId, userId);
        Assert.False(result.IsSuccess);
        Assert.Equal(CreateOrderFailureReason.NoSeatsHeld, result.FailureReason);
    }

    [Fact]
    public async Task CreateOrderFromHolds_SalesClosed_ReturnsFailure()
    {
        var (db, _, service, now) = CreateTestHarness();
        const int userId = 42;
        const int showtimeId = 10;

        var evt = new Event { Id = 1, Name = "Concert", Description = "Desc", Location = "Hall", Status = EventStatus.Published };
        var showtime = new Showtime { Id = showtimeId, EventId = 1, Event = evt, StartTime = now.AddHours(2), Status = ShowtimeStatus.Closed };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        await db.SaveChangesAsync();

        var result = await service.CreateOrderFromHoldsAsync(showtimeId, userId);
        Assert.False(result.IsSuccess);
        Assert.Equal(CreateOrderFailureReason.SalesClosed, result.FailureReason);
    }

    [Fact]
    public async Task SeatingChartQuery_SeatBelongingToPaidOrder_DisplaysAsSold()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var reservationService = new SeatReservationService(db, clockMock.Object);

        const int showtimeId = 10;
        var evt = new Event { Id = 1, Name = "Concert", Description = "Desc", Location = "Hall", Status = EventStatus.Published };
        var showtime = new Showtime { Id = showtimeId, EventId = 1, Event = evt, StartTime = fixedTime.AddHours(2), Status = ShowtimeStatus.OnSale };
        var category = new SeatCategory { Id = 1, ShowtimeId = showtimeId, Name = "Standard", NormalizedName = "STANDARD", Price = 200000m };

        var seat = new Seat
        {
            Id = 101,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Available // In seats table it might not yet have been flipped to Sold, but Order is Paid
        };

        var paidOrder = new Order
        {
            Id = 1,
            UserId = 99,
            ShowtimeId = showtimeId,
            Status = OrderStatus.Paid,
            TotalAmount = 200000m,
            ExpiresAt = fixedTime.AddHours(1),
            CreatedAt = fixedTime.AddHours(-1)
        };

        var orderItem = new OrderItem
        {
            Id = 1,
            OrderId = 1,
            Order = paidOrder,
            SeatId = 101,
            ShowtimeId = showtimeId,
            UnitPrice = 200000m,
            Status = OrderStatus.Paid
        };

        db.Events.Add(evt);
        db.Showtimes.Add(showtime);
        db.SeatCategories.Add(category);
        db.Seats.Add(seat);
        db.Orders.Add(paidOrder);
        db.OrderItems.Add(orderItem);
        await db.SaveChangesAsync();

        // T-19 / T-36 integration check
        var chart = await reservationService.GetSeatingChartStatusAsync(showtimeId);

        Assert.Single(chart);
        Assert.Equal("Sold", chart[0].DisplayStatus);
        Assert.False(chart[0].CanSelect);
    }

    [Fact]
    public async Task OrdersController_CreateOrder_Returns201_WhenNewOrderCreated()
    {
        var mockService = new Mock<IOrderService>();
        var orderDto = new OrderDto(
            1, 42, 10, "Pending", 200000m,
            DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow, DateTime.UtcNow, 900, []);

        mockService.Setup(s => s.CreateOrderFromHoldsAsync(10, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOrderResult.Success(orderDto, isExistingPendingOrder: false));

        var controller = new OrdersController(mockService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "42"),
                        new Claim(ClaimTypes.Role, "Buyer")
                    ], "TestAuth"))
                }
            }
        };

        var response = await controller.CreateOrderForShowtime(10, CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status201Created, objectResult.StatusCode);
    }

    [Fact]
    public async Task OrdersController_CreateOrder_Returns200_WhenExistingPendingOrder()
    {
        var mockService = new Mock<IOrderService>();
        var orderDto = new OrderDto(
            1, 42, 10, "Pending", 200000m,
            DateTime.UtcNow.AddMinutes(15), DateTime.UtcNow, DateTime.UtcNow, 900, []);

        mockService.Setup(s => s.CreateOrderFromHoldsAsync(10, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOrderResult.Success(orderDto, isExistingPendingOrder: true));

        var controller = new OrdersController(mockService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "42"),
                        new Claim(ClaimTypes.Role, "Buyer")
                    ], "TestAuth"))
                }
            }
        };

        var response = await controller.CreateOrderForShowtime(10, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(response);
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
    }

    [Fact]
    public async Task OrdersController_CreateOrder_Returns409_WhenHoldsExpired()
    {
        var mockService = new Mock<IOrderService>();
        mockService.Setup(s => s.CreateOrderFromHoldsAsync(10, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOrderResult.Failed(
                CreateOrderFailureReason.SeatHoldExpired,
                "Ghế đã hết hạn",
                DateTime.UtcNow,
                [new LostSeatDto(101, "A", "1")]));

        var controller = new OrdersController(mockService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "42"),
                        new Claim(ClaimTypes.Role, "Buyer")
                    ], "TestAuth"))
                }
            }
        };

        var response = await controller.CreateOrderForShowtime(10, CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetOrderByIdAsync_WithFailedPaymentEvent_ReturnsHasFailedPaymentsTrue()
    {
        var (db, _, service, now) = CreateTestHarness();
        const int userId = 42;
        const int orderId = 1;

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = 10,
            Status = OrderStatus.Pending,
            TotalAmount = 250000m,
            ExpiresAt = now.AddMinutes(15),
            CreatedAt = now
        };

        var failedEvent = new PaymentEvent
        {
            TransactionId = "TXN-FAIL-001",
            OrderId = orderId,
            Amount = 250000m,
            Success = false,
            RawPayload = "1|250000|TXN-FAIL-001|false",
            CreatedAt = now
        };

        db.Orders.Add(order);
        db.PaymentEvents.Add(failedEvent);
        await db.SaveChangesAsync();

        var result = await service.GetOrderByIdAsync(orderId, userId);

        Assert.NotNull(result);
        Assert.True(result.HasFailedPayments);
        Assert.True(result.HasFailedPayment);
        Assert.Equal(1, result.FailedPaymentCount);
        Assert.NotNull(result.PaymentEvents);
        Assert.Single(result.PaymentEvents);
        Assert.Equal("TXN-FAIL-001", result.PaymentEvents[0].TransactionId);
        Assert.False(result.PaymentEvents[0].Success);
    }

    [Fact]
    public async Task GetOrderByIdAsync_WithoutFailedPaymentEvent_ReturnsHasFailedPaymentsFalse()
    {
        var (db, _, service, now) = CreateTestHarness();
        const int userId = 42;
        const int orderId = 2;

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = 10,
            Status = OrderStatus.Pending,
            TotalAmount = 250000m,
            ExpiresAt = now.AddMinutes(15),
            CreatedAt = now
        };

        var successEvent = new PaymentEvent
        {
            TransactionId = "TXN-OK-001",
            OrderId = orderId,
            Amount = 250000m,
            Success = true,
            RawPayload = "2|250000|TXN-OK-001|true",
            CreatedAt = now
        };

        db.Orders.Add(order);
        db.PaymentEvents.Add(successEvent);
        await db.SaveChangesAsync();

        var result = await service.GetOrderByIdAsync(orderId, userId);

        Assert.NotNull(result);
        Assert.False(result.HasFailedPayments);
        Assert.False(result.HasFailedPayment);
        Assert.Equal(0, result.FailedPaymentCount);
        Assert.NotNull(result.PaymentEvents);
        Assert.Single(result.PaymentEvents);
        Assert.True(result.PaymentEvents[0].Success);
    }

    [Fact]
    public async Task GetPendingOrderForShowtimeAsync_WithFailedPaymentEvent_ReturnsHasFailedPaymentsTrue()
    {
        var (db, _, service, now) = CreateTestHarness();
        const int userId = 42;
        const int orderId = 3;
        const int showtimeId = 10;

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = showtimeId,
            Status = OrderStatus.Pending,
            TotalAmount = 250000m,
            ExpiresAt = now.AddMinutes(15),
            CreatedAt = now
        };

        var failedEvent = new PaymentEvent
        {
            TransactionId = "TXN-FAIL-003",
            OrderId = orderId,
            Amount = 250000m,
            Success = false,
            RawPayload = "3|250000|TXN-FAIL-003|false",
            CreatedAt = now
        };

        db.Orders.Add(order);
        db.PaymentEvents.Add(failedEvent);
        await db.SaveChangesAsync();

        var result = await service.GetPendingOrderForShowtimeAsync(showtimeId, userId);

        Assert.NotNull(result);
        Assert.True(result.HasFailedPayments);
        Assert.True(result.HasFailedPayment);
        Assert.Equal(1, result.FailedPaymentCount);
    }
}
