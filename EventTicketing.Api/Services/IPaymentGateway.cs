namespace EventTicketing.Api.Services;

public interface IPaymentGateway
{
    string GatewayName { get; }
    string CreatePaymentUrl(int orderId, string orderCode, decimal amount, string returnUrl);
    string GenerateWebhookSignature(string payload);
    bool VerifyWebhookSignature(string payload, string signature);
}
