namespace EventTicketing.Api.Configuration;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    public string SandboxBaseUrl { get; set; } = "http://127.0.0.1:5173/sandbox-gateway";
    public string ReturnUrl { get; set; } = "http://127.0.0.1:5173/orders/result";
}
