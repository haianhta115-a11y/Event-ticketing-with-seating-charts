using EventTicketing.Api.Models.Orders;

namespace EventTicketing.Api.Services;

public interface IPaymentService
{
    Task<InitiatePaymentResult> InitiatePaymentAsync(
        int orderId,
        int userId,
        CancellationToken cancellationToken = default);

    Task<ConfirmPaymentResult> ConfirmPaymentAsync(
        PaymentResultRequest request,
        CancellationToken cancellationToken = default);

    Task<ProcessWebhookResult> ProcessWebhookAsync(
        WebhookPaymentRequest request,
        CancellationToken cancellationToken = default);
}
