using System.Globalization;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class MockPaymentGatewayTests
{
    private static Mock<IWebHostEnvironment> CreateEnvironmentMock(string envName)
    {
        var envMock = new Mock<IWebHostEnvironment>();
        envMock.SetupGet(e => e.EnvironmentName).Returns(envName);
        return envMock;
    }

    [Fact]
    public void Production_WithMockGateway_ThrowsInvalidOperationException()
    {
        // T-44 (SCRUM-114): Giả sử môi trường là production, Khi khởi động với cổng giả lập, Thì ứng dụng từ chối khởi động
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Mock"
            })
            .Build();

        var envMock = CreateEnvironmentMock("Production");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PaymentGatewayConfiguration.ConfigurePaymentGateway(services, config, envMock.Object));

        Assert.Contains("Production", ex.Message);
        Assert.Contains("Mock", ex.Message);
    }

    [Fact]
    public void Production_WithSandboxGateway_ConfiguresSuccessfully()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Sandbox"
            })
            .Build();

        var envMock = CreateEnvironmentMock("Production");

        var options = PaymentGatewayConfiguration.ConfigurePaymentGateway(services, config, envMock.Object);

        Assert.Equal("Sandbox", options.Provider);
        var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IPaymentGateway>();
        Assert.Equal("Sandbox", gateway.GatewayName);
    }

    [Fact]
    public void Development_WithMockGateway_ConfiguresSuccessfully()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:Provider"] = "Mock"
            })
            .Build();

        var envMock = CreateEnvironmentMock("Development");

        var options = PaymentGatewayConfiguration.ConfigurePaymentGateway(services, config, envMock.Object);

        Assert.Equal("Mock", options.Provider);
        var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IPaymentGateway>();
        Assert.Equal("Mock", gateway.GatewayName);
    }

    [Fact]
    public void EnvironmentVariable_OverridesConfigurationProvider()
    {
        var originalEnv = Environment.GetEnvironmentVariable("PAYMENT_GATEWAY");
        try
        {
            Environment.SetEnvironmentVariable("PAYMENT_GATEWAY", "Mock");

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Payments:Provider"] = "Sandbox"
                })
                .Build();

            var envMock = CreateEnvironmentMock("Development");

            var options = PaymentGatewayConfiguration.ConfigurePaymentGateway(services, config, envMock.Object);

            Assert.Equal("Mock", options.Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAYMENT_GATEWAY", originalEnv);
        }
    }

    [Fact]
    public void MockPaymentGateway_CreatePaymentUrl_GeneratesCorrectUrl()
    {
        var options = Options.Create(new PaymentOptions
        {
            MockBaseUrl = "http://127.0.0.1:5173/mock-gateway",
            ReturnUrl = "http://127.0.0.1:5173/orders"
        });

        var gateway = new MockPaymentGateway(options);
        var url = gateway.CreatePaymentUrl(42, "ORD-42", 500000m, "http://127.0.0.1:5173/orders");

        Assert.StartsWith("http://127.0.0.1:5173/mock-gateway?", url);
        Assert.Contains("orderId=42", url);
        Assert.Contains("orderCode=ORD-42", url);
        Assert.Contains("amount=500000", url);
        Assert.Contains("returnUrl=", url);
    }

    [Fact]
    public void MockPaymentGateway_SignatureVerification_ValidatesSuccessfully()
    {
        var options = Options.Create(new PaymentOptions
        {
            WebhookSecret = "test-secret-key-123"
        });

        var gateway = new MockPaymentGateway(options);
        var payload = "42|500000|TXN-001|true";

        var signature = gateway.GenerateWebhookSignature(payload);

        Assert.NotNull(signature);
        Assert.NotEmpty(signature);
        Assert.True(gateway.VerifyWebhookSignature(payload, signature));
        Assert.False(gateway.VerifyWebhookSignature(payload, "invalid-tampered-signature"));
        Assert.False(gateway.VerifyWebhookSignature("altered-payload", signature));
        Assert.False(gateway.VerifyWebhookSignature(payload, ""));
    }

    [Fact]
    public async Task HandleMockWebhook_WithInvalidSignature_Returns401Unauthorized()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var clockMock = new Mock<IReservationClock>();
        var options = Options.Create(new PaymentOptions());
        var loggerMock = new Mock<ILogger<PaymentService>>();
        var gateway = new MockPaymentGateway(options);
        var paymentService = new PaymentService(db, clockMock.Object, options, loggerMock.Object, gateway);

        var envMock = CreateEnvironmentMock("Development");
        var configMock = new Mock<IConfiguration>();

        var controller = new PaymentsController(paymentService, envMock.Object, configMock.Object, gateway);

        var request = new MockWebhookRequest(
            OrderId: 10,
            TransactionId: "TXN-001",
            Amount: 200000m,
            Success: true,
            Signature: "wrong-signature");

        var result = await controller.HandleMockWebhook(request, null, CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objResult.StatusCode);
    }

    [Fact]
    public async Task HandleMockWebhook_WithValidSignature_ConfirmsPayment()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var fixedNow = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var clockMock = new Mock<IReservationClock>();
        clockMock.Setup(c => c.NowAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fixedNow);

        var options = Options.Create(new PaymentOptions());
        var loggerMock = new Mock<ILogger<PaymentService>>();
        var gateway = new MockPaymentGateway(options);
        var paymentService = new PaymentService(db, clockMock.Object, options, loggerMock.Object, gateway);

        var seat = new Seat
        {
            Id = 501,
            ShowtimeId = 1,
            SeatCategoryId = 1,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Held,
            HeldByUserId = 99,
            HeldUntil = fixedNow.AddMinutes(10)
        };
        db.Seats.Add(seat);

        var order = new Order
        {
            Id = 77,
            UserId = 99,
            ShowtimeId = 1,
            TotalAmount = 250000m,
            Status = OrderStatus.Pending,
            CreatedAt = fixedNow,
            ExpiresAt = fixedNow.AddMinutes(15),
            OrderItems =
            [
                new OrderItem
                {
                    Id = 1,
                    OrderId = 77,
                    SeatId = 501,
                    Seat = seat,
                    UnitPrice = 250000m,
                    Status = OrderStatus.Pending
                }
            ]
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var envMock = CreateEnvironmentMock("Development");
        var configMock = new Mock<IConfiguration>();

        var controller = new PaymentsController(paymentService, envMock.Object, configMock.Object, gateway);

        var payload = $"77|{250000m.ToString(CultureInfo.InvariantCulture)}|MOCK-TXN-77|true";
        var signature = gateway.GenerateWebhookSignature(payload);

        var request = new MockWebhookRequest(
            OrderId: 77,
            TransactionId: "MOCK-TXN-77",
            Amount: 250000m,
            Success: true,
            Signature: signature);

        var result = await controller.HandleMockWebhook(request, null, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        // Verify database state: order paid, seat sold
        var updatedOrder = await db.Orders.FindAsync(77);
        Assert.NotNull(updatedOrder);
        Assert.Equal(OrderStatus.Paid, updatedOrder.Status);
        Assert.Equal(250000m, updatedOrder.PaidAmount);

        var updatedSeat = await db.Seats.FindAsync(501);
        Assert.NotNull(updatedSeat);
        Assert.Equal(SeatStatus.Sold, updatedSeat.Status);
        Assert.Null(updatedSeat.HeldByUserId);
    }
}
