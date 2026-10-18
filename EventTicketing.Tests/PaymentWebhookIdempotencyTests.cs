using System.Globalization;
using System.Text.Json;
using EventTicketing.Api.Configuration;
using EventTicketing.Api.Controllers;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Orders;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class PaymentWebhookIdempotencyTests
{
    private static JsonElement ToJson(object? value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement;
    }

    private static (string DbName, AppDbContext Db, Mock<IReservationClock> ClockMock, PaymentService Service, PaymentsController Controller, IPaymentGateway Gateway, DateTime FixedNow)
        CreateTestHarness(PaymentOptions? paymentOptions = null, string? dbName = null)
    {
        var sharedDbName = dbName ?? Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: sharedDbName)
            .Options;

        var db = new AppDbContext(options);
        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var opts = paymentOptions ?? new PaymentOptions { WebhookSecret = "super-secret-test-key-32charslong!" };
        var optionsWrapper = Options.Create(opts);
        var gateway = new MockPaymentGateway(optionsWrapper);
        var loggerMock = new Mock<ILogger<PaymentService>>();
        var service = new PaymentService(db, clockMock.Object, optionsWrapper, loggerMock.Object, gateway);

        var envMock = new Mock<IWebHostEnvironment>();
        envMock.SetupGet(e => e.EnvironmentName).Returns("Development");
        var configMock = new Mock<IConfiguration>();

        var controller = new PaymentsController(service, envMock.Object, configMock.Object, gateway);

        return (sharedDbName, db, clockMock, service, controller, gateway, fixedTime);
    }

    private static async Task<(Order Order, List<Seat> Seats, List<OrderItem> Items)> CreateOrderWithSeatsAsync(
        AppDbContext db,
        DateTime now,
        int orderId = 201,
        int userId = 88,
        int showtimeId = 15,
        int seatCount = 2,
        OrderStatus orderStatus = OrderStatus.Pending,
        DateTime? expiresAt = null,
        decimal unitPrice = 150000m)
    {
        var seats = new List<Seat>();
        var items = new List<OrderItem>();
        var totalAmount = unitPrice * seatCount;
        var orderExpires = expiresAt ?? now.AddMinutes(15);

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = showtimeId,
            Status = orderStatus,
            TotalAmount = totalAmount,
            CreatedAt = now.AddMinutes(-5),
            ExpiresAt = orderExpires
        };

        if (!await db.Showtimes.AnyAsync(s => s.Id == showtimeId))
        {
            db.Showtimes.Add(new Showtime
            {
                Id = showtimeId,
                EventId = 1,
                StartTime = now.AddDays(1),
                EndTime = now.AddDays(1).AddHours(2),
                Status = ShowtimeStatus.OnSale
            });
        }

        if (!await db.SeatCategories.AnyAsync(sc => sc.Id == 1 && sc.ShowtimeId == showtimeId))
        {
            db.SeatCategories.Add(new SeatCategory
            {
                Id = 1,
                ShowtimeId = showtimeId,
                Name = "VIP",
                NormalizedName = "VIP",
                Price = unitPrice
            });
        }

        for (var i = 1; i <= seatCount; i++)
        {
            var seat = new Seat
            {
                Id = 1000 + orderId * 10 + i,
                ShowtimeId = showtimeId,
                SeatCategoryId = 1,
                Row = "B",
                Number = i.ToString(),
                Status = orderStatus == OrderStatus.Pending ? SeatStatus.Held : SeatStatus.Available,
                HeldByUserId = orderStatus == OrderStatus.Pending ? userId : null,
                HeldUntil = orderStatus == OrderStatus.Pending ? orderExpires : null
            };

            var item = new OrderItem
            {
                Id = 5000 + orderId * 10 + i,
                OrderId = order.Id,
                SeatId = seat.Id,
                ShowtimeId = showtimeId,
                UnitPrice = unitPrice,
                Status = orderStatus,
                Seat = seat
            };

            seats.Add(seat);
            items.Add(item);
            order.OrderItems.Add(item);
            db.Seats.Add(seat);
            db.OrderItems.Add(item);
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();

        return (order, seats, items);
    }

    /// <summary>
    /// SCRUM-117 & SCRUM-36 AC 1:
    /// Giả sử cổng gửi lại cùng một webhook 5 lần tuần tự,
    /// Khi xử lý xong, Thì chỉ có một bản ghi thanh toán, đơn đổi trạng thái một lần và vé chỉ sinh một bộ.
    /// </summary>
    [Fact]
    public async Task Webhook_SequentialRetries5Times_OnlyOnePaymentEventRecorded_OnlyOneTicketSet_All200Ok()
    {
        var (_, db, _, _, controller, gateway, now) = CreateTestHarness();
        var (order, seats, items) = await CreateOrderWithSeatsAsync(db, now, orderId: 301, seatCount: 2, unitPrice: 200000m);

        var transactionId = "TXN-MOCK-RETRY-5X";
        var payload = $"{order.Id}|{order.TotalAmount.ToString(CultureInfo.InvariantCulture)}|{transactionId}|true";
        var signature = gateway.GenerateWebhookSignature(payload);

        var webhookRequest = new MockWebhookRequest(
            OrderId: order.Id,
            TransactionId: transactionId,
            Amount: order.TotalAmount,
            Success: true,
            Signature: signature);

        var responses = new List<IActionResult>();

        // Gửi cùng một webhook 5 lần tuần tự
        for (var i = 0; i < 5; i++)
        {
            var response = await controller.HandleMockWebhook(webhookRequest, null, CancellationToken.None);
            responses.Add(response);
        }

        // 1. Cả 5 lần gửi đều trả về HTTP 200 OK
        Assert.Equal(5, responses.Count);
        foreach (var resp in responses)
        {
            var okResult = Assert.IsType<OkObjectResult>(resp);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode ?? 200);
        }

        // 2. Lần đầu xử lý thành công, 4 lần sau nhận diện trùng lặp (isDuplicate = true)
        var firstJson = ToJson(((OkObjectResult)responses[0]).Value);
        Assert.True(firstJson.GetProperty("success").GetBoolean());
        Assert.False(firstJson.GetProperty("isDuplicate").GetBoolean());
        Assert.Equal("Paid", firstJson.GetProperty("outcome").GetString());

        for (var i = 1; i < 5; i++)
        {
            var json = ToJson(((OkObjectResult)responses[i]).Value);
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.True(json.GetProperty("isDuplicate").GetBoolean());
            Assert.Equal("DuplicateIgnored", json.GetProperty("outcome").GetString());
        }

        // 3. Đúng duy nhất 1 bản ghi trong bảng payment_events
        var paymentEvents = await db.PaymentEvents.ToListAsync();
        Assert.Single(paymentEvents);
        Assert.Equal(transactionId, paymentEvents[0].TransactionId);
        Assert.Equal(order.Id, paymentEvents[0].OrderId);
        Assert.Equal(order.TotalAmount, paymentEvents[0].Amount);

        // 4. Đơn đổi trạng thái đúng 1 lần sang Paid
        var updatedOrder = await db.Orders.FindAsync(order.Id);
        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(order.TotalAmount, updatedOrder.PaidAmount);
        Assert.Equal(transactionId, updatedOrder.PaymentReference);

        // 5. Vé chỉ sinh đúng một bộ (2 vé cho 2 ghế của đơn, không nhân đôi)
        var paidItems = await db.OrderItems.Where(oi => oi.OrderId == order.Id && oi.Status == OrderStatus.Paid).ToListAsync();
        Assert.Equal(2, paidItems.Count);

        var soldSeats = await db.Seats.IgnoreQueryFilters().Where(s => (s.Id == seats[0].Id || s.Id == seats[1].Id) && s.Status == SeatStatus.Sold).ToListAsync();
        Assert.Equal(2, soldSeats.Count);
    }

    /// <summary>
    /// SCRUM-117 & SCRUM-36 AC 2:
    /// Giả sử hai hoặc nhiều bản sao của cùng webhook tới đồng thời,
    /// Khi xử lý song song, Thì đúng một bản được xử lý, bản kia bị bỏ qua — không có lỗi 500.
    /// </summary>
    [Fact]
    public async Task Webhook_ParallelConcurrent5Requests_ExactlyOneProcessed_No500Errors_OnlyOnePaymentEvent_OnlyOneTicketSet()
    {
        var (sharedDbName, mainDb, clockMock, _, _, gateway, now) = CreateTestHarness();
        var (order, seats, _) = await CreateOrderWithSeatsAsync(mainDb, now, orderId: 302, seatCount: 3, unitPrice: 100000m);

        var transactionId = "TXN-MOCK-PARALLEL-5X";
        var payload = $"{order.Id}|{order.TotalAmount.ToString(CultureInfo.InvariantCulture)}|{transactionId}|true";
        var signature = gateway.GenerateWebhookSignature(payload);

        var webhookReq = new WebhookPaymentRequest(
            OrderId: order.Id,
            TransactionId: transactionId,
            Amount: order.TotalAmount,
            Success: true,
            RawPayload: payload,
            Signature: signature);

        // Mô phỏng 5 luồng request song song độc lập với 5 DbContext riêng biệt cùng trỏ vào 1 database
        var opts = Options.Create(new PaymentOptions { WebhookSecret = "super-secret-test-key-32charslong!" });
        var tasks = Enumerable.Range(0, 5)
            .Select(async _ =>
            {
                var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(databaseName: sharedDbName)
                    .Options;
                await using var taskDb = new AppDbContext(dbOptions);
                var taskService = new PaymentService(taskDb, clockMock.Object, opts, null, gateway);
                return await taskService.ProcessWebhookAsync(webhookReq, CancellationToken.None);
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // 1. Không có ngoại lệ hoặc lỗi 500 nào xảy ra
        Assert.Equal(5, results.Length);

        // 2. Đúng 1 bản được xử lý chính thức (IsDuplicate = false), 4 bản bị bỏ qua (IsDuplicate = true)
        var processedCount = results.Count(r => !r.IsDuplicate && r.Outcome == ProcessWebhookOutcome.Processed);
        var duplicateCount = results.Count(r => r.IsDuplicate && r.Outcome == ProcessWebhookOutcome.DuplicateIgnored);

        Assert.Equal(1, processedCount);
        Assert.Equal(4, duplicateCount);

        // 3. Đúng một bản ghi trong payment_events
        var paymentEvents = await mainDb.PaymentEvents.ToListAsync();
        Assert.Single(paymentEvents);
        Assert.Equal(transactionId, paymentEvents[0].TransactionId);

        // 4. Đúng một bộ vé được sinh ra
        var paidItems = await mainDb.OrderItems.Where(oi => oi.OrderId == order.Id && oi.Status == OrderStatus.Paid).ToListAsync();
        Assert.Equal(3, paidItems.Count);

        var soldSeats = await mainDb.Seats.IgnoreQueryFilters().Where(s => s.Status == SeatStatus.Sold).ToListAsync();
        Assert.Equal(3, soldSeats.Count);
    }

    /// <summary>
    /// SCRUM-116 & SCRUM-36 AC 3:
    /// Giả sử webhook tới cho đơn đã bị huỷ vì hết hạn,
    /// Khi xử lý, Thì không đổi đơn thành đã trả mà đánh dấu cần kiểm tra để hoàn tiền.
    /// </summary>
    [Fact]
    public async Task Webhook_ForExpiredOrCancelledOrder_MarksOrderForReview_DoesNotMarkPaid_NoTicketsSold()
    {
        var (_, db, _, _, controller, gateway, now) = CreateTestHarness();
        // Đơn đã bị huỷ do hết hạn
        var (order, seats, items) = await CreateOrderWithSeatsAsync(
            db,
            now,
            orderId: 303,
            seatCount: 2,
            orderStatus: OrderStatus.Expired,
            expiresAt: now.AddMinutes(-30));

        var transactionId = "TXN-MOCK-EXPIRED-ORDER";
        var payload = $"{order.Id}|{order.TotalAmount.ToString(CultureInfo.InvariantCulture)}|{transactionId}|true";
        var signature = gateway.GenerateWebhookSignature(payload);

        var webhookRequest = new MockWebhookRequest(
            OrderId: order.Id,
            TransactionId: transactionId,
            Amount: order.TotalAmount,
            Success: true,
            Signature: signature);

        var response = await controller.HandleMockWebhook(webhookRequest, null, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(response);

        // 1. Trả 200 OK để cổng thanh toán ngừng gửi lại
        Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode ?? 200);

        // 2. Đơn KHÔNG đổi thành Paid, giữ nguyên Expired
        var updatedOrder = await db.Orders.FindAsync(order.Id);
        Assert.NotNull(updatedOrder);
        Assert.NotEqual(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(OrderStatus.Expired, updatedOrder.Status);

        // 3. Đánh dấu cần kiểm tra để hoàn tiền (RequiresPaymentReview = true)
        Assert.True(updatedOrder.RequiresPaymentReview);
        Assert.Contains("hoàn tiền", updatedOrder.PaymentReviewReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(order.TotalAmount, updatedOrder.PaidAmount);
        Assert.Equal(transactionId, updatedOrder.PaymentReference);

        // 4. Ghế KHÔNG bị đổi thành Sold (vé không sinh ra)
        var soldSeats = await db.Seats.Where(s => s.Status == SeatStatus.Sold).ToListAsync();
        Assert.Empty(soldSeats);

        var paidItems = await db.OrderItems.Where(oi => oi.Status == OrderStatus.Paid).ToListAsync();
        Assert.Empty(paidItems);

        // 5. Sự kiện thanh toán vẫn được ghi nhận trong payment_events để đối soát S-51
        var paymentEvents = await db.PaymentEvents.ToListAsync();
        Assert.Single(paymentEvents);
        Assert.Equal(transactionId, paymentEvents[0].TransactionId);
    }

    /// <summary>
    /// SCRUM-36 AC 4:
    /// Giả sử webhook tới trước khi người dùng quay lại trang kết quả,
    /// Khi người dùng quay lại, Thì thấy đơn đã trả.
    /// </summary>
    [Fact]
    public async Task Webhook_ArrivesBeforeUserReturns_UserReturnsAndSeesOrderPaid()
    {
        var (_, db, _, _, controller, gateway, now) = CreateTestHarness();
        var (order, seats, _) = await CreateOrderWithSeatsAsync(db, now, orderId: 304, seatCount: 1, unitPrice: 250000m);

        var transactionId = "TXN-WEBHOOK-BEFORE-USER";
        var payload = $"{order.Id}|{order.TotalAmount.ToString(CultureInfo.InvariantCulture)}|{transactionId}|true";
        var signature = gateway.GenerateWebhookSignature(payload);

        // 1. Webhook tới trước
        var webhookRequest = new MockWebhookRequest(
            OrderId: order.Id,
            TransactionId: transactionId,
            Amount: order.TotalAmount,
            Success: true,
            Signature: signature);

        var webhookResponse = await controller.HandleMockWebhook(webhookRequest, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(webhookResponse);

        // 2. Người dùng sau đó quay lại trang kết quả thanh toán sandbox
        var userReturnRequest = new PaymentResultRequest(order.Id, transactionId, order.TotalAmount, true);
        var returnResponse = await controller.HandleSandboxPaymentResult(userReturnRequest, CancellationToken.None);
        var okReturnResult = Assert.IsType<OkObjectResult>(returnResponse);

        var returnJson = ToJson(okReturnResult.Value);
        Assert.True(returnJson.GetProperty("success").GetBoolean());
        Assert.Equal("AlreadyPaid", returnJson.GetProperty("outcome").GetString());

        // 3. Đơn hàng hiển thị là đã thanh toán
        var updatedOrder = await db.Orders.FindAsync(order.Id);
        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(order.TotalAmount, updatedOrder.PaidAmount);
    }

    /// <summary>
    /// SCRUM-115 / T-45:
    /// Bảng payment_events lưu mã giao dịch với ràng buộc unique.
    /// Tiêu chí: Chèn hai bản ghi cùng mã giao dịch thì bản thứ hai bị từ chối.
    /// </summary>
    [Fact]
    public async Task PaymentEvents_UniqueConstraint_RejectsDuplicateTransactionIdDirectInsert()
    {
        var (_, db, _, _, _, _, _) = CreateTestHarness();

        var event1 = new PaymentEvent
        {
            TransactionId = "TXN-UNIQUE-TEST-001",
            OrderId = 999,
            Amount = 100000m,
            Success = true,
            RawPayload = "raw-data-1",
            CreatedAt = DateTime.UtcNow
        };

        var event2 = new PaymentEvent
        {
            TransactionId = "TXN-UNIQUE-TEST-001", // Trùng mã giao dịch
            OrderId = 999,
            Amount = 100000m,
            Success = true,
            RawPayload = "raw-data-2",
            CreatedAt = DateTime.UtcNow
        };

        db.PaymentEvents.Add(event1);
        await db.SaveChangesAsync();

        db.PaymentEvents.Add(event2);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("IX_payment_events_transaction_id", ex.Message);
    }

    /// <summary>
    /// Kiểm tra chữ ký webhook không hợp lệ thì từ chối 401 Unauthorized ngay đầu handler, không ghi nhận gì.
    /// </summary>
    [Fact]
    public async Task Webhook_InvalidSignature_Returns401Unauthorized_NoEventRecorded()
    {
        var (_, db, _, _, controller, _, now) = CreateTestHarness();
        var (order, _, _) = await CreateOrderWithSeatsAsync(db, now, orderId: 305);

        var webhookRequest = new MockWebhookRequest(
            OrderId: order.Id,
            TransactionId: "TXN-INVALID-SIG",
            Amount: order.TotalAmount,
            Success: true,
            Signature: "completely-invalid-signature");

        var response = await controller.HandleMockWebhook(webhookRequest, null, CancellationToken.None);
        var statusResult = Assert.IsType<ObjectResult>(response);

        Assert.Equal(StatusCodes.Status401Unauthorized, statusResult.StatusCode);

        // Bảng payment_events hoàn toàn không có bản ghi nào
        var count = await db.PaymentEvents.CountAsync();
        Assert.Equal(0, count);

        // Đơn hàng vẫn ở trạng thái Pending
        var updatedOrder = await db.Orders.FindAsync(order.Id);
        Assert.Equal(OrderStatus.Pending, updatedOrder!.Status);
    }
}
