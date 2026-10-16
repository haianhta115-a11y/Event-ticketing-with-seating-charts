namespace EventTicketing.Api.Models.Orders;

public sealed record PaymentRedirectDto(
    int OrderId,
    string OrderCode,
    decimal Amount,
    string RedirectUrl);

public sealed record PaymentResultRequest(
    int OrderId,
    string? TransactionId,
    decimal Amount,
    bool Success);

public sealed record MockWebhookRequest(
    int OrderId,
    string? TransactionId,
    decimal Amount,
    bool Success,
    string? Signature = null);

public sealed record MockSignRequest(
    int OrderId,
    string? TransactionId,
    decimal Amount,
    bool Success);

public sealed record MockSignResponse(
    string Signature,
    string Payload);

public enum InitiatePaymentOutcome
{
    Ok,
    NotFound,
    NotPayable,
    Expired
}

public sealed record InitiatePaymentResult(
    InitiatePaymentOutcome Outcome,
    PaymentRedirectDto? Data = null,
    string? ErrorMessage = null);

public enum ConfirmPaymentOutcome
{
    Paid,
    AlreadyPaid,
    AmountMismatch,
    NeedsReview,
    GatewayFailed,
    OrderNotFound
}

public sealed record ConfirmPaymentResult(
    ConfirmPaymentOutcome Outcome,
    string? Message = null);
