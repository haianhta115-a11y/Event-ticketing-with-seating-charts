using System.Globalization;
using System.Text.Json;
using EventTicketing.Api.Models.Orders;
using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class PaymentsController(
    IPaymentService paymentService,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ILogger<PaymentsController>? logger = null,
    IPaymentGateway? paymentGateway = null) : ControllerBase
{
    private readonly IPaymentGateway _gateway = paymentGateway ?? new MockPaymentGateway();

    [HttpPost("orders/{id:int}/pay")]
    [Authorize(Policy = AccessPolicies.SeatBooking)]
    public async Task<IActionResult> PayOrder(int id, CancellationToken ct)
    {
        var userId = AccessPolicies.UserId(User);
        if (!userId.HasValue) return Unauthorized();

        var result = await paymentService.InitiatePaymentAsync(id, userId.Value, ct);

        return result.Outcome switch
        {
            InitiatePaymentOutcome.Ok => Ok(result.Data),
            InitiatePaymentOutcome.NotFound => NotFound(new
            {
                success = false,
                errorCode = "ORDER_NOT_FOUND",
                message = result.ErrorMessage
            }),
            InitiatePaymentOutcome.Expired => StatusCode(StatusCodes.Status409Conflict, new
            {
                success = false,
                errorCode = "ORDER_EXPIRED",
                message = result.ErrorMessage
            }),
            InitiatePaymentOutcome.NotPayable => StatusCode(StatusCodes.Status409Conflict, new
            {
                success = false,
                errorCode = "ORDER_NOT_PAYABLE",
                message = result.ErrorMessage
            }),
            _ => BadRequest(new
            {
                success = false,
                message = result.ErrorMessage
            })
        };
    }

    [HttpPost("payments/sandbox/result")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleSandboxPaymentResult([FromBody] PaymentResultRequest request, CancellationToken ct)
    {
        var allowSandbox = environment.IsDevelopment() || configuration.GetValue<bool>("Payments:AllowAnonymousSandboxResult");
        if (!allowSandbox)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                message = "Cổng thanh toán sandbox chỉ khả dụng ở môi trường Development hoặc khi được bật cấu hình."
            });
        }

        var result = await paymentService.ConfirmPaymentAsync(request, ct);

        if (result.Outcome == ConfirmPaymentOutcome.OrderNotFound)
        {
            return NotFound(new
            {
                success = false,
                outcome = result.Outcome.ToString(),
                message = result.Message
            });
        }

        var isSuccess = result.Outcome is ConfirmPaymentOutcome.Paid or ConfirmPaymentOutcome.AlreadyPaid;

        return Ok(new
        {
            success = isSuccess,
            outcome = result.Outcome.ToString(),
            message = result.Message
        });
    }

    /// <summary>
    /// S-19 / SCRUM-35: Webhook cho cổng thanh toán giả lập có kiểm tra chữ ký hợp lệ.
    /// S-21 / SCRUM-37 (T-48, T-49): Kiểm chữ ký trước khi đọc nội dung; ghi log kèm IP khi từ chối.
    /// </summary>
    [HttpPost("payments/mock/webhook")]
    [HttpPost("payments/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleMockWebhook(
        [FromBody] MockWebhookRequest request,
        [FromHeader(Name = "X-Webhook-Signature")] string? headerSignature,
        CancellationToken ct)
    {
        // T-48: Đọc chữ ký từ body hoặc header, kiểm tra trước khi xử lý nghiệp vụ.
        var signature = request.Signature ?? headerSignature;
        var payload = $"{request.OrderId}|{request.Amount.ToString(CultureInfo.InvariantCulture)}|{request.TransactionId}|{request.Success.ToString().ToLowerInvariant()}";
        var remoteIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";

        if (!_gateway.VerifyWebhookSignature(payload, signature ?? string.Empty))
        {
            // T-49: Log lần từ chối kèm IP nguồn – KHÔNG log nội dung nhạy cảm / PII / secret.
            logger?.LogWarning(
                "Webhook signature rejected. RemoteIP={RemoteIP}, StatusCode={StatusCode}, Reason={Reason}, Timestamp={TimestampUtc}",
                remoteIp,
                401,
                "INVALID_SIGNATURE",
                DateTime.UtcNow);

            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                success = false,
                errorCode = "INVALID_SIGNATURE",
                message = "Chữ ký webhook không hợp lệ."
            });
        }

        var result = await paymentService.ConfirmPaymentAsync(
            new PaymentResultRequest(request.OrderId, request.TransactionId, request.Amount, request.Success),
            ct);

        if (result.Outcome == ConfirmPaymentOutcome.OrderNotFound)
        {
            // T-49: Chữ ký đúng nhưng đơn hàng không tồn tại – log kèm mã đơn và IP.
            logger?.LogWarning(
                "Webhook order not found. RemoteIP={RemoteIP}, StatusCode={StatusCode}, OrderId={OrderId}, Reason={Reason}, Timestamp={TimestampUtc}",
                remoteIp,
                404,
                request.OrderId,
                "ORDER_NOT_FOUND",
                DateTime.UtcNow);

            return NotFound(new
            {
                success = false,
                outcome = result.Outcome.ToString(),
                message = result.Message
            });
        }

        var isSuccess = result.Outcome is ConfirmPaymentOutcome.Paid or ConfirmPaymentOutcome.AlreadyPaid;

        return Ok(new
        {
            success = isSuccess,
            outcome = result.Outcome.ToString(),
            message = result.Message
        });
    }

    /// <summary>
    /// S-19 / SCRUM-35: Ký webhook giả lập để mô phỏng chữ ký hợp lệ từ cổng thanh toán
    /// </summary>
    [HttpPost("payments/mock/sign")]
    [AllowAnonymous]
    public IActionResult GenerateMockSignature([FromBody] MockSignRequest request)
    {
        var payload = $"{request.OrderId}|{request.Amount.ToString(CultureInfo.InvariantCulture)}|{request.TransactionId}|{request.Success.ToString().ToLowerInvariant()}";
        var signature = _gateway.GenerateWebhookSignature(payload);
        return Ok(new MockSignResponse(signature, payload));
    }
}
