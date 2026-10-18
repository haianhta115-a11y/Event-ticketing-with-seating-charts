using System.Security.Claims;
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
public sealed class PaymentServiceTests
{
    private static (AppDbContext Db, Mock<IReservationClock> ClockMock, PaymentService Service, DateTime FixedNow) CreateTestHarness(PaymentOptions? paymentOptions = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var db = new AppDbContext(options);
        var clockMock = new Mock<IReservationClock>();
        var fixedTime = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedTime);

        var optionsWrapper = Options.Create(paymentOptions ?? new PaymentOptions());
        var loggerMock = new Mock<ILogger<PaymentService>>();
        var service = new PaymentService(db, clockMock.Object, optionsWrapper, loggerMock.Object);
        return (db, clockMock, service, fixedTime);
    }

    private static async Task<(Order Order, Seat Seat, OrderItem Item)> CreateSampleOrderAsync(
        AppDbContext db,
        int orderId = 1,
        int userId = 42,
        int showtimeId = 10,
        OrderStatus orderStatus = OrderStatus.Pending,
        DateTime? expiresAt = null,
        decimal totalAmount = 250000m,
        SeatStatus seatStatus = SeatStatus.Held,
        int? seatHeldBy = null)
    {
        var seat = new Seat
        {
            Id = 100 + orderId,
            ShowtimeId = showtimeId,
            SeatCategoryId = 1,
            Row = "A",
            Number = orderId.ToString(),
            Status = seatStatus,
            HeldByUserId = seatHeldBy ?? (seatStatus == SeatStatus.Held ? userId : null),
            HeldUntil = seatStatus == SeatStatus.Held ? (expiresAt ?? DateTime.UtcNow.AddMinutes(15)) : null
        };

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            ShowtimeId = showtimeId,
            Status = orderStatus,
            TotalAmount = totalAmount,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(15)
        };

        var item = new OrderItem
        {
            Id = 1000 + orderId,
            OrderId = order.Id,
            SeatId = seat.Id,
            ShowtimeId = showtimeId,
            UnitPrice = totalAmount,
            Status = orderStatus,
            Seat = seat
        };

        order.OrderItems.Add(item);

        db.Seats.Add(seat);
        db.Orders.Add(order);
        db.OrderItems.Add(item);
        await db.SaveChangesAsync();

        return (order, seat, item);
    }

    // 1. Initiate: đơn Pending còn hạn -> URL chứa đúng amount và ORD-{id}
    [Fact]
    public async Task InitiatePayment_PendingAndValid_ReturnsRedirectUrlWithAmountAndOrderCode()
    {
        var (db, _, service, now) = CreateTestHarness();
        var (order, _, _) = await CreateSampleOrderAsync(db, orderId: 101, userId: 42, expiresAt: now.AddMinutes(10), totalAmount: 350000m);

        var result = await service.InitiatePaymentAsync(order.Id, 42);

        Assert.Equal(InitiatePaymentOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Data);
        Assert.Equal(101, result.Data.OrderId);
        Assert.Equal("ORD-101", result.Data.OrderCode);
        Assert.Equal(350000m, result.Data.Amount);
        Assert.Contains("orderId=101", result.Data.RedirectUrl);
        Assert.Contains("orderCode=ORD-101", result.Data.RedirectUrl);
        Assert.Contains("amount=350000", result.Data.RedirectUrl);
        Assert.Contains("returnUrl=", result.Data.RedirectUrl);
    }

    // 2. Initiate: đơn hết hạn -> Expired; đơn đã Paid -> NotPayable; đơn của user khác -> NotFound
    [Fact]
    public async Task InitiatePayment_Expired_ReturnsExpired()
    {
        var (db, _, service, now) = CreateTestHarness();
        var (order, _, _) = await CreateSampleOrderAsync(db, orderId: 102, userId: 42, expiresAt: now.AddMinutes(-5));

        var result = await service.InitiatePaymentAsync(order.Id, 42);

        Assert.Equal(InitiatePaymentOutcome.Expired, result.Outcome);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task InitiatePayment_AlreadyPaid_ReturnsNotPayable()
    {
        var (db, _, service, now) = CreateTestHarness();
        var (order, _, _) = await CreateSampleOrderAsync(db, orderId: 103, userId: 42, orderStatus: OrderStatus.Paid, expiresAt: now.AddMinutes(10));

        var result = await service.InitiatePaymentAsync(order.Id, 42);

        Assert.Equal(InitiatePaymentOutcome.NotPayable, result.Outcome);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task InitiatePayment_DifferentUser_ReturnsNotFound()
    {
        var (db, _, service, now) = CreateTestHarness();
        var (order, _, _) = await CreateSampleOrderAsync(db, orderId: 104, userId: 42, expiresAt: now.AddMinutes(10));

        var result = await service.InitiatePaymentAsync(order.Id, userId: 999);

        Assert.Equal(InitiatePaymentOutcome.NotFound, result.Outcome);
        Assert.Null(result.Data);
    }

    // 3. Confirm đúng tiền -> đơn Paid, ghế Sold, HeldByUserId/HeldUntil null, OrderItem Paid
    [Fact]
    public async Task ConfirmPayment_ExactAmount_MarksOrderPaidAndSeatsSold()
    {
        var (db, _, service, now) = CreateTestHarness();
        var (order, seat, item) = await CreateSampleOrderAsync(db, orderId: 105, totalAmount: 500000m);

        var request = new PaymentResultRequest(order.Id, "TXN-999", 500000m, true);
        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.Paid, result.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);
        var updatedItem = await db.OrderItems.FindAsync(item.Id);

        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(now, updatedOrder.PaidAt);
        Assert.Equal(500000m, updatedOrder.PaidAmount);
        Assert.Equal("TXN-999", updatedOrder.PaymentReference);

        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Sold, updatedSeat.Status);
        Assert.Null(updatedSeat.HeldByUserId);
        Assert.Null(updatedSeat.HeldUntil);

        Assert.NotNull(updatedItem);
        Assert.Equal(OrderStatus.Paid, updatedItem.Status);
    }

    // 4. Confirm lần hai cùng đơn -> AlreadyPaid, dữ liệu không đổi (idempotent)
    [Fact]
    public async Task ConfirmPayment_SecondTime_ReturnsAlreadyPaid_Idempotent()
    {
        var (db, _, service, now) = CreateTestHarness();
        var (order, seat, _) = await CreateSampleOrderAsync(db, orderId: 106, totalAmount: 200000m);

        var request = new PaymentResultRequest(order.Id, "TXN-IDEMP", 200000m, true);

        // First call
        var firstResult = await service.ConfirmPaymentAsync(request);
        Assert.Equal(ConfirmPaymentOutcome.Paid, firstResult.Outcome);

        // Second call
        var secondResult = await service.ConfirmPaymentAsync(request);
        Assert.Equal(ConfirmPaymentOutcome.AlreadyPaid, secondResult.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);

        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(now, updatedOrder.PaidAt);
        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Sold, updatedSeat.Status);
    }

    // 5. Confirm không có người dùng nào đăng nhập (kịch bản 3: gọi thẳng service) -> vẫn Paid
    [Fact]
    public async Task ConfirmPayment_WithoutUserContext_SuccessfullyConfirmsPayment()
    {
        var (db, _, service, _) = CreateTestHarness();
        var (order, seat, _) = await CreateSampleOrderAsync(db, orderId: 107, totalAmount: 400000m);

        var request = new PaymentResultRequest(order.Id, "TXN-WEBHOOK-NO-USER", 400000m, true);
        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.Paid, result.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);

        Assert.Equal(OrderStatus.Paid, updatedOrder!.Status);
        Assert.Equal(SeatStatus.Sold, updatedSeat!.Status);
    }

    // 6. Confirm sai tiền (nhỏ hơn và lớn hơn) -> AmountMismatch, đơn vẫn Pending, ghế vẫn Held, RequiresPaymentReview = true
    [Fact]
    public async Task ConfirmPayment_AmountMismatch_SmallerAmount_FlagsReviewAndLeavesSeatHeld()
    {
        var (db, _, service, _) = CreateTestHarness();
        var (order, seat, _) = await CreateSampleOrderAsync(db, orderId: 108, totalAmount: 300000m);

        var request = new PaymentResultRequest(order.Id, "TXN-LESS", 200000m, true);
        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.AmountMismatch, result.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);

        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Pending, updatedOrder.Status);
        Assert.True(updatedOrder.RequiresPaymentReview);
        Assert.False(string.IsNullOrWhiteSpace(updatedOrder.PaymentReviewReason));
        Assert.Equal(200000m, updatedOrder.PaidAmount);
        Assert.Equal("TXN-LESS", updatedOrder.PaymentReference);

        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Held, updatedSeat.Status);
        Assert.Equal(42, updatedSeat.HeldByUserId);
    }

    [Fact]
    public async Task ConfirmPayment_AmountMismatch_GreaterAmount_FlagsReviewAndLeavesSeatHeld()
    {
        var (db, _, service, _) = CreateTestHarness();
        var (order, seat, _) = await CreateSampleOrderAsync(db, orderId: 109, totalAmount: 300000m);

        var request = new PaymentResultRequest(order.Id, "TXN-MORE", 500000m, true);
        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.AmountMismatch, result.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);

        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Pending, updatedOrder.Status);
        Assert.True(updatedOrder.RequiresPaymentReview);
        Assert.False(string.IsNullOrWhiteSpace(updatedOrder.PaymentReviewReason));
        Assert.Equal(500000m, updatedOrder.PaidAmount);

        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Held, updatedSeat.Status);
    }

    // 7. Confirm khi ghế đã không còn Held -> NeedsReview, ghế không bị đổi sang Sold
    [Fact]
    public async Task ConfirmPayment_SeatNotHeld_FlagsNeedsReview_SeatNotSold()
    {
        var (db, _, service, _) = CreateTestHarness();
        // Ghế ở trạng thái Available (ví dụ hết hạn bị nhả)
        var (order, seat, _) = await CreateSampleOrderAsync(db, orderId: 110, totalAmount: 250000m, seatStatus: SeatStatus.Available);

        var request = new PaymentResultRequest(order.Id, "TXN-LOST-SEAT", 250000m, true);
        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.NeedsReview, result.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);

        Assert.NotNull(updatedOrder);
        Assert.True(updatedOrder.RequiresPaymentReview);
        Assert.False(string.IsNullOrWhiteSpace(updatedOrder.PaymentReviewReason));
        Assert.Equal(250000m, updatedOrder.PaidAmount);

        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Available, updatedSeat.Status);
    }

    // 8. Success=false -> GatewayFailed, không đổi dữ liệu
    [Fact]
    public async Task ConfirmPayment_GatewayFailed_ReturnsGatewayFailed_DoesNotChangeData()
    {
        var (db, _, service, _) = CreateTestHarness();
        var (order, seat, _) = await CreateSampleOrderAsync(db, orderId: 111, totalAmount: 250000m);

        var request = new PaymentResultRequest(order.Id, "TXN-FAILED", 250000m, false);
        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.GatewayFailed, result.Outcome);

        var updatedOrder = await db.Orders.FindAsync(order.Id);
        var updatedSeat = await db.Seats.FindAsync(seat.Id);

        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Pending, updatedOrder.Status);
        Assert.False(updatedOrder.RequiresPaymentReview);
        Assert.Null(updatedOrder.PaidAmount);

        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Held, updatedSeat.Status);
    }

    // 9. OrderNotFound
    [Fact]
    public async Task ConfirmPayment_NonExistentOrder_ReturnsOrderNotFound()
    {
        var (_, _, service, _) = CreateTestHarness();
        var request = new PaymentResultRequest(999999, "TXN-NONE", 100000m, true);

        var result = await service.ConfirmPaymentAsync(request);

        Assert.Equal(ConfirmPaymentOutcome.OrderNotFound, result.Outcome);
    }

    // PaymentsController Tests
    [Fact]
    public async Task PaymentsController_PayOrder_Returns200WithDto_WhenInitiateOk()
    {
        var mockService = new Mock<IPaymentService>();
        var dto = new PaymentRedirectDto(1, "ORD-1", 100000m, "http://gateway.test/pay");
        mockService.Setup(s => s.InitiatePaymentAsync(1, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InitiatePaymentResult(InitiatePaymentOutcome.Ok, dto));

        var mockEnv = new Mock<IWebHostEnvironment>();
        var mockConfig = new Mock<IConfiguration>();

        var controller = new PaymentsController(mockService.Object, mockEnv.Object, mockConfig.Object, new Mock<ILogger<PaymentsController>>().Object)
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

        var response = await controller.PayOrder(1, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(response);
        var returnedDto = Assert.IsType<PaymentRedirectDto>(okResult.Value);
        Assert.Equal("ORD-1", returnedDto.OrderCode);
    }

    [Fact]
    public async Task PaymentsController_PayOrder_Returns409_WhenExpired()
    {
        var mockService = new Mock<IPaymentService>();
        mockService.Setup(s => s.InitiatePaymentAsync(1, 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InitiatePaymentResult(InitiatePaymentOutcome.Expired, ErrorMessage: "Đơn hết hạn"));

        var mockEnv = new Mock<IWebHostEnvironment>();
        var mockConfig = new Mock<IConfiguration>();

        var controller = new PaymentsController(mockService.Object, mockEnv.Object, mockConfig.Object, new Mock<ILogger<PaymentsController>>().Object)
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

        var response = await controller.PayOrder(1, CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
    }

    [Fact]
    public async Task PaymentsController_SandboxResult_Returns403_WhenNonDevAndNotEnabled()
    {
        var mockService = new Mock<IPaymentService>();
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.EnvironmentName).Returns("Production");
        var mockConfig = new Mock<IConfiguration>();
        mockConfig.Setup(c => c.GetSection(It.IsAny<string>())).Returns(new Mock<IConfigurationSection>().Object);

        var controller = new PaymentsController(mockService.Object, mockEnv.Object, mockConfig.Object, new Mock<ILogger<PaymentsController>>().Object);
        var request = new PaymentResultRequest(1, "TXN-PROD", 100000m, true);

        var response = await controller.HandleSandboxPaymentResult(request, CancellationToken.None);
        var objectResult = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task PaymentsController_SandboxResult_Returns200_WhenDevelopment()
    {
        var mockService = new Mock<IPaymentService>();
        mockService.Setup(s => s.ConfirmPaymentAsync(It.IsAny<PaymentResultRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConfirmPaymentResult(ConfirmPaymentOutcome.Paid, "Thành công"));

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.EnvironmentName).Returns("Development");
        var mockConfig = new Mock<IConfiguration>();

        var controller = new PaymentsController(mockService.Object, mockEnv.Object, mockConfig.Object, new Mock<ILogger<PaymentsController>>().Object);
        var request = new PaymentResultRequest(1, "TXN-DEV", 100000m, true);

        var response = await controller.HandleSandboxPaymentResult(request, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(response);
        Assert.NotNull(okResult.Value);
    }
}
