using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EventTicketing.Api.Configuration;
using Microsoft.Extensions.Options;

namespace EventTicketing.Api.Services;

public sealed class SandboxPaymentGateway(IOptions<PaymentOptions>? options = null) : IPaymentGateway
{
    private readonly PaymentOptions _options = options?.Value ?? new PaymentOptions();

    public string GatewayName => "Sandbox";

    public string CreatePaymentUrl(int orderId, string orderCode, decimal amount, string returnUrl)
    {
        var baseUrl = _options.SandboxBaseUrl;
        var separator = baseUrl.Contains('?') ? "&" : "?";
        var finalReturnUrl = returnUrl.Contains("{orderId}")
            ? returnUrl.Replace("{orderId}", orderId.ToString())
            : (returnUrl.EndsWith('/') ? $"{returnUrl}{orderId}" : $"{returnUrl}/{orderId}");

        return $"{baseUrl}{separator}orderId={orderId}&orderCode={Uri.EscapeDataString(orderCode)}&amount={amount.ToString(CultureInfo.InvariantCulture)}&returnUrl={Uri.EscapeDataString(finalReturnUrl)}";
    }

    public string GenerateWebhookSignature(string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(_options.WebhookSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var expectedSignature = GenerateWebhookSignature(payload);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);
        var actualBytes = Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant());

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
