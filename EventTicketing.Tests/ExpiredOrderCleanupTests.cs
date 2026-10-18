using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class ExpiredOrderCleanupTests
{
    private static (AppDbContext Db, Mock<IReservationClock> ClockMock, ExpiredOrderCleanup Service, DateTime FixedNow) CreateTestHarness()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 18, 14, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var loggerMock = new Mock<ILogger<ExpiredOrderCleanup>>();
        var service = new ExpiredOrderCleanup(db, clockMock.Object, loggerMock.Object);

        return (db, clockMock, service, fixedTime);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeOrders_OnlyExpiredPendingOrderIsCancelledAndSeatsReleased()
    {
        // T-53 (S-23): Test tích hợp tạo ba đơn: đã trả, chờ còn hạn, chờ quá hạn; chạy job; kiểm chỉ đơn quá hạn đổi.
        var (db, _, service, now) = CreateTestHarness();

        // 1. Đơn 1: Đã trả tiền (Paid), dù ExpiresAt đã qua thì đơn và ghế vẫn không bị đụng tới (AC2)
        var seat1 = new Seat
        {
            Id = 101,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Sold,
            HeldByUserId = null
        };
        var order1 = new Order
        {
            Id = 1,
            UserId = 10,
            ShowtimeId = 1,
            Status = OrderStatus.Paid,
            TotalAmount = 200000m,
            CreatedAt = now.AddMinutes(-30),
            ExpiresAt = now.AddMinutes(-10),
            OrderItems = [new OrderItem { Id = 1, OrderId = 1, SeatId = 101, Seat = seat1, Status = OrderStatus.Paid }]
        };

        // 2. Đơn 2: Đang chờ nhưng CÒN HẠN (ExpiresAt > now) -> không bị đụng tới
        var seat2 = new Seat
        {
            Id = 102,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "A",
            Number = "2",
            Status = SeatStatus.Held,
            HeldByUserId = 20,
            HeldUntil = now.AddMinutes(10)
        };
        var order2 = new Order
        {
            Id = 2,
            UserId = 20,
            ShowtimeId = 1,
            Status = OrderStatus.Pending,
            TotalAmount = 200000m,
            CreatedAt = now.AddMinutes(-5),
            ExpiresAt = now.AddMinutes(10),
            OrderItems = [new OrderItem { Id = 2, OrderId = 2, SeatId = 102, Seat = seat2, Status = OrderStatus.Pending }]
        };

        // 3. Đơn 3: Đang chờ và ĐÃ QUÁ HẠN (ExpiresAt <= now) -> phải bị đổi thành Expired và nhả ghế về Available
        var seat3 = new Seat
        {
            Id = 103,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "A",
            Number = "3",
            Status = SeatStatus.Held,
            HeldByUserId = 30,
            HeldUntil = now.AddMinutes(-5)
        };
        var order3 = new Order
        {
            Id = 3,
            UserId = 30,
            ShowtimeId = 1,
            Status = OrderStatus.Pending,
            TotalAmount = 200000m,
            CreatedAt = now.AddMinutes(-20),
            ExpiresAt = now.AddMinutes(-5),
            OrderItems = [new OrderItem { Id = 3, OrderId = 3, SeatId = 103, Seat = seat3, Status = OrderStatus.Pending }]
        };

        db.Seats.AddRange(seat1, seat2, seat3);
        db.Orders.AddRange(order1, order2, order3);
        await db.SaveChangesAsync();

        // Chạy job dọn dẹp
        var count = await service.ExecuteAsync();

        // Kiểm tra kết quả
        Assert.Equal(1, count);

        // Kiểm tra Đơn 1 (Paid) không đổi
        var updatedOrder1 = await db.Orders.Include(o => o.OrderItems).FirstOrDefaultAsync(o => o.Id == 1);
        Assert.NotNull(updatedOrder1);
        Assert.Equal(OrderStatus.Paid, updatedOrder1.Status);
        var updatedSeat1 = await db.Seats.FindAsync(101);
        Assert.NotNull(updatedSeat1);
        Assert.Equal(SeatStatus.Sold, updatedSeat1.Status);

        // Kiểm tra Đơn 2 (Pending còn hạn) không đổi
        var updatedOrder2 = await db.Orders.Include(o => o.OrderItems).FirstOrDefaultAsync(o => o.Id == 2);
        Assert.NotNull(updatedOrder2);
        Assert.Equal(OrderStatus.Pending, updatedOrder2.Status);
        var updatedSeat2 = await db.Seats.FindAsync(102);
        Assert.NotNull(updatedSeat2);
        Assert.Equal(SeatStatus.Held, updatedSeat2.Status);
        Assert.Equal(20, updatedSeat2.HeldByUserId);

        // Kiểm tra Đơn 3 (Pending quá hạn) bị hủy và nhả ghế
        var updatedOrder3 = await db.Orders.Include(o => o.OrderItems).FirstOrDefaultAsync(o => o.Id == 3);
        Assert.NotNull(updatedOrder3);
        Assert.Equal(OrderStatus.Expired, updatedOrder3.Status);
        Assert.Equal(OrderStatus.Expired, updatedOrder3.OrderItems.First().Status);

        var updatedSeat3 = await db.Seats.FindAsync(103);
        Assert.NotNull(updatedSeat3);
        Assert.Equal(SeatStatus.Available, updatedSeat3.Status);
        Assert.Null(updatedSeat3.HeldByUserId);
        Assert.Null(updatedSeat3.HeldUntil);
    }

    [Fact]
    public async Task ExecuteAsync_IsIdempotent_SecondRunChangesNothing()
    {
        // AC: job chạy lặp lại được nhiều lần trên cùng dữ liệu mà kết quả không đổi
        var (db, _, service, now) = CreateTestHarness();

        var seat = new Seat
        {
            Id = 201,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "B",
            Number = "1",
            Status = SeatStatus.Held,
            HeldByUserId = 40,
            HeldUntil = now.AddMinutes(-2)
        };
        var order = new Order
        {
            Id = 10,
            UserId = 40,
            ShowtimeId = 1,
            Status = OrderStatus.Pending,
            TotalAmount = 150000m,
            CreatedAt = now.AddMinutes(-17),
            ExpiresAt = now.AddMinutes(-2),
            OrderItems = [new OrderItem { Id = 10, OrderId = 10, SeatId = 201, Seat = seat, Status = OrderStatus.Pending }]
        };

        db.Seats.Add(seat);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // Lần chạy 1: Hủy 1 đơn
        var firstRun = await service.ExecuteAsync();
        Assert.Equal(1, firstRun);

        // Lần chạy 2 ngay sau đó: Không còn đơn quá hạn nào
        var secondRun = await service.ExecuteAsync();
        Assert.Equal(0, secondRun);

        var checkOrder = await db.Orders.FindAsync(10);
        Assert.NotNull(checkOrder);
        Assert.Equal(OrderStatus.Expired, checkOrder.Status);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleSeatsInExpiredOrder_ReleasesAllSeats()
    {
        var (db, _, service, now) = CreateTestHarness();

        var s1 = new Seat { Id = 301, ShowtimeId = 1, SeatCategoryId = 1, Row = "C", Number = "1", Status = SeatStatus.Held, HeldByUserId = 50 };
        var s2 = new Seat { Id = 302, ShowtimeId = 1, SeatCategoryId = 1, Row = "C", Number = "2", Status = SeatStatus.Held, HeldByUserId = 50 };
        var s3 = new Seat { Id = 303, ShowtimeId = 1, SeatCategoryId = 1, Row = "C", Number = "3", Status = SeatStatus.Held, HeldByUserId = 50 };

        var order = new Order
        {
            Id = 20,
            UserId = 50,
            ShowtimeId = 1,
            Status = OrderStatus.Pending,
            TotalAmount = 450000m,
            CreatedAt = now.AddMinutes(-20),
            ExpiresAt = now.AddMinutes(-1),
            OrderItems =
            [
                new OrderItem { Id = 21, OrderId = 20, SeatId = 301, Seat = s1, Status = OrderStatus.Pending },
                new OrderItem { Id = 22, OrderId = 20, SeatId = 302, Seat = s2, Status = OrderStatus.Pending },
                new OrderItem { Id = 23, OrderId = 20, SeatId = 303, Seat = s3, Status = OrderStatus.Pending }
            ]
        };

        db.Seats.AddRange(s1, s2, s3);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var count = await service.ExecuteAsync();
        Assert.Equal(1, count);

        var seats = await db.Seats.Where(s => s.Id >= 301 && s.Id <= 303).ToListAsync();
        Assert.All(seats, s =>
        {
            Assert.Equal(SeatStatus.Available, s.Status);
            Assert.Null(s.HeldByUserId);
            Assert.Null(s.HeldUntil);
        });
    }

    [Fact]
    public async Task ExecuteAsync_RaceCondition_OrderConcurrentlyPaid_NotOverwrittenToExpired()
    {
        // T-53: Thêm ca đua: nếu đơn hàng đã được cập nhật sang Paid ngay trước khi xử lý, không bị hủy
        var (db, _, service, now) = CreateTestHarness();

        var seat = new Seat
        {
            Id = 401,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "D",
            Number = "1",
            Status = SeatStatus.Sold,
            HeldByUserId = null
        };
        var order = new Order
        {
            Id = 30,
            UserId = 60,
            ShowtimeId = 1,
            Status = OrderStatus.Paid, // Đã thanh toán do webhook vừa xử lý
            TotalAmount = 300000m,
            CreatedAt = now.AddMinutes(-20),
            ExpiresAt = now.AddMinutes(-1),
            OrderItems = [new OrderItem { Id = 31, OrderId = 30, SeatId = 401, Seat = seat, Status = OrderStatus.Paid }]
        };

        db.Seats.Add(seat);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var count = await service.ExecuteAsync();
        Assert.Equal(0, count);

        var checkOrder = await db.Orders.FindAsync(30);
        Assert.NotNull(checkOrder);
        Assert.Equal(OrderStatus.Paid, checkOrder.Status);

        var checkSeat = await db.Seats.FindAsync(401);
        Assert.NotNull(checkSeat);
        Assert.Equal(SeatStatus.Sold, checkSeat.Status);
    }
}
