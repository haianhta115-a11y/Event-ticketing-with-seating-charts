using System.Globalization;
using System.Net;
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

/// <summary>
/// S-21 (SCRUM-37): Webhook có chữ ký sai bị từ chối.
/// T-48 (SCRUM-118): Kiểm chữ ký webhook trước khi đọc nội dung.
/// T-49 (SCRUM-119): Ghi log lần từ chối kèm địa chỉ nguồn.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WebhookSignatureTests
{
    private const string TestSecret = "test-webhook-secret-s21";

    private static PaymentsController CreateController(
        IPaymentService paymentService,
        MockPaymentGateway gateway,
        Mock<ILogger<PaymentsController>> loggerMock,
        IPAddress? remoteIp = null)
    {
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.SetupGet(e => e.EnvironmentName).Returns("Development");
        var configMock = new Mock<IConfiguration>();

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = remoteIp ?? IPAddress.Parse("203.0.113.42");

        var controller = new PaymentsController(
            paymentService, envMock.Object, configMock.Object, loggerMock.Object, gateway)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };

        return controller;
    }

    private static string BuildPayload(int orderId, decimal amount, string? transactionId, bool success)
        => $"{orderId}|{amount.ToString(CultureInfo.InvariantCulture)}|{transactionId}|{success.ToString().ToLowerInvariant()}";

    private static (AppDbContext Db, PaymentService Service, MockPaymentGateway Gateway, Mock<ILogger<PaymentsController>> LoggerMock) CreateTestHarness()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(dbOptions);

        var paymentOptions = Options.Create(new PaymentOptions { WebhookSecret = TestSecret });
        var gateway = new MockPaymentGateway(paymentOptions);

        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var serviceLoggerMock = new Mock<ILogger<PaymentService>>();
        var service = new PaymentService(db, clockMock.Object, paymentOptions, serviceLoggerMock.Object, gateway);
        var controllerLoggerMock = new Mock<ILogger<PaymentsController>>();

        return (db, service, gateway, controllerLoggerMock);
    }

    private static async Task<Order> SeedOrderAsync(AppDbContext db, int orderId = 1, int userId = 99, decimal totalAmount = 250000m)
    {
        var fixedTime = new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

        var seat = new Seat
        {
            Id = 500 + orderId,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "A",
            Number = orderId.ToString(),
            Status = SeatStatus.Held,
            HeldByUserId = userId,
            HeldUntil = fixedTime.AddMinutes(15)
        };
        db.Seats.Add(seat);

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = 1,
            TotalAmount = totalAmount,
            Status = OrderStatus.Pending,
            CreatedAt = fixedTime,
            ExpiresAt = fixedTime.AddMinutes(15),
            OrderItems =
            [
                new OrderItem
                {
                    Id = 1000 + orderId,
                    OrderId = orderId,
                    SeatId = seat.Id,
                    ShowtimeId = 1,
                    Seat = seat,
                    UnitPrice = totalAmount,
                    Status = OrderStatus.Pending
                }
            ]
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    /// <summary>
    /// Test 1 – Payload bị sửa 1 ký tự: trả 401, đơn hàng không đổi, không có bản ghi mới, có log cảnh báo kèm IP.
    /// AC 1: Chữ ký không khớp → HTTP 401, không đọc nội dung nghiệp vụ, không đổi trạng thái đơn hàng.
    /// </summary>
    [Fact]
    public async Task Webhook_TamperedPayload_Returns401_OrderUnchanged_LogsWarningWithIP()
    {
        var (db, service, gateway, loggerMock) = CreateTestHarness();
        var order = await SeedOrderAsync(db, orderId: 10, totalAmount: 200000m);

        // Ký payload gốc
        var originalPayload = BuildPayload(10, 200000m, "TXN-TAMPER", true);
        var validSignature = gateway.GenerateWebhookSignature(originalPayload);

        // Tạo request có payload bị sửa 1 ký tự (amount thay đổi)
        var controller = CreateController(service, gateway, loggerMock);
        var tamperedRequest = new MockWebhookRequest(
            OrderId: 10,
            TransactionId: "TXN-TAMPER",
            Amount: 200001m,  // bị sửa 1 ký tự so với payload đã ký
            Success: true,
            Signature: validSignature);

        var result = await controller.HandleMockWebhook(tamperedRequest, null, CancellationToken.None);

        // Assert: 401 Unauthorized
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objResult.StatusCode);

        // Assert: Đơn hàng không đổi trạng thái
        var unchanged = await db.Orders.FindAsync(10);
        Assert.NotNull(unchanged);
        Assert.Equal(OrderStatus.Pending, unchanged.Status);
        Assert.Null(unchanged.PaidAt);
        Assert.Null(unchanged.PaidAmount);

        // Assert: Ghế vẫn Held
        var unchangedSeat = await db.Seats.FindAsync(510);
        Assert.NotNull(unchangedSeat);
        Assert.Equal(SeatStatus.Held, unchangedSeat.Status);
        Assert.Equal(99, unchangedSeat.HeldByUserId);

        // Assert: Log cảnh báo có chứa thông tin IP
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v != null && (v.ToString() ?? "").Contains("203.0.113.42") && (v.ToString() ?? "").Contains("INVALID_SIGNATURE")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Test 2 – Chữ ký sai / thiếu: trả 401, ghi log kèm IP.
    /// </summary>
    [Theory]
    [InlineData("completely-wrong-signature", "wrong signature")]
    [InlineData("", "empty signature")]
    [InlineData(null, "null signature")]
    public async Task Webhook_InvalidOrMissingSignature_Returns401_LogsWarningWithIP(string? signature, string scenario)
    {
        var (db, service, gateway, loggerMock) = CreateTestHarness();

        var controller = CreateController(service, gateway, loggerMock, IPAddress.Parse("10.0.0.5"));
        var request = new MockWebhookRequest(
            OrderId: 1,
            TransactionId: "TXN-BAD",
            Amount: 100000m,
            Success: true,
            Signature: signature);

        var result = await controller.HandleMockWebhook(request, null, CancellationToken.None);

        // Assert: 401 Unauthorized
        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objResult.StatusCode);

        // Assert: Log cảnh báo kèm IP
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v != null && (v.ToString() ?? "").Contains("10.0.0.5") && (v.ToString() ?? "").Contains("INVALID_SIGNATURE")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            $"Scenario: {scenario}");
    }

    /// <summary>
    /// Test 3 – Đơn hàng không tồn tại: chữ ký hợp lệ nhưng orderId không có trong DB → 404, log kèm mã đơn và IP.
    /// AC 2: Chữ ký đúng, mã đơn không tồn tại → HTTP 404, ghi log.
    /// </summary>
    [Fact]
    public async Task Webhook_ValidSignature_OrderNotFound_Returns404_LogsWithOrderIdAndIP()
    {
        var (db, service, gateway, loggerMock) = CreateTestHarness();
        // Không seed bất kỳ đơn hàng nào – orderId 999 không tồn tại

        var nonExistentOrderId = 999;
        var payload = BuildPayload(nonExistentOrderId, 300000m, "TXN-GHOST", true);
        var validSignature = gateway.GenerateWebhookSignature(payload);

        var controller = CreateController(service, gateway, loggerMock, IPAddress.Parse("192.168.1.100"));
        var request = new MockWebhookRequest(
            OrderId: nonExistentOrderId,
            TransactionId: "TXN-GHOST",
            Amount: 300000m,
            Success: true,
            Signature: validSignature);

        var result = await controller.HandleMockWebhook(request, null, CancellationToken.None);

        // Assert: 404 Not Found
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, notFoundResult.StatusCode);

        // Assert: Log có chứa OrderId và IP
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v != null &&
                    (v.ToString() ?? "").Contains("192.168.1.100") &&
                    (v.ToString() ?? "").Contains("ORDER_NOT_FOUND") &&
                    (v.ToString() ?? "").Contains("999")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Test 4 – Luồng hợp lệ: chữ ký đúng và đơn hàng tồn tại → xử lý bình thường (T-46).
    /// </summary>
    [Fact]
    public async Task Webhook_ValidSignature_ValidOrder_ProcessesNormally()
    {
        var (db, service, gateway, loggerMock) = CreateTestHarness();
        var order = await SeedOrderAsync(db, orderId: 50, totalAmount: 500000m);

        var payload = BuildPayload(50, 500000m, "TXN-VALID-50", true);
        var validSignature = gateway.GenerateWebhookSignature(payload);

        var controller = CreateController(service, gateway, loggerMock);
        var request = new MockWebhookRequest(
            OrderId: 50,
            TransactionId: "TXN-VALID-50",
            Amount: 500000m,
            Success: true,
            Signature: validSignature);

        var result = await controller.HandleMockWebhook(request, null, CancellationToken.None);

        // Assert: 200 OK với outcome Paid
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        // Assert: Đơn hàng chuyển sang Paid
        var updatedOrder = await db.Orders.FindAsync(50);
        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(500000m, updatedOrder.PaidAmount);
        Assert.Equal("TXN-VALID-50", updatedOrder.PaymentReference);

        // Assert: Ghế chuyển sang Sold
        var updatedSeat = await db.Seats.FindAsync(550);
        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Sold, updatedSeat.Status);
        Assert.Null(updatedSeat.HeldByUserId);

        // Assert: Không có log cảnh báo INVALID_SIGNATURE
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v != null && (v.ToString() ?? "").Contains("INVALID_SIGNATURE")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Test bổ sung – Chữ ký qua header X-Webhook-Signature (thay vì qua body) cũng được xác thực đúng.
    /// </summary>
    [Fact]
    public async Task Webhook_SignatureViaHeader_ValidatesCorrectly()
    {
        var (db, service, gateway, loggerMock) = CreateTestHarness();
        var order = await SeedOrderAsync(db, orderId: 60, totalAmount: 150000m);

        var payload = BuildPayload(60, 150000m, "TXN-HDR-60", true);
        var validSignature = gateway.GenerateWebhookSignature(payload);

        var controller = CreateController(service, gateway, loggerMock);

        // Signature qua body = null, chỉ qua header
        var request = new MockWebhookRequest(
            OrderId: 60,
            TransactionId: "TXN-HDR-60",
            Amount: 150000m,
            Success: true,
            Signature: null);

        var result = await controller.HandleMockWebhook(request, validSignature, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        var updatedOrder = await db.Orders.FindAsync(60);
        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
    }
}
