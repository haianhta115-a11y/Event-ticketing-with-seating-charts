using System.Globalization;
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
    /// S-19 / SCRUM-35: Webhook cho cổng thanh toán giả lập có kiểm tra chữ ký hợp lệ
    /// </summary>
    [HttpPost("payments/mock/webhook")]
    [HttpPost("payments/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleMockWebhook(
        [FromBody] MockWebhookRequest request,
        [FromHeader(Name = "X-Webhook-Signature")] string? headerSignature,
        CancellationToken ct)
    {
        var signature = request.Signature ?? headerSignature;
        var payload = $"{request.OrderId}|{request.Amount.ToString(CultureInfo.InvariantCulture)}|{request.TransactionId}|{request.Success.ToString().ToLowerInvariant()}";

        if (!_gateway.VerifyWebhookSignature(payload, signature ?? string.Empty))
        {
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
