namespace EventTicketing.Api.Configuration;

public sealed class OrderOptions
{
    public const string SectionName = "Orders";
    public int PaymentTimeoutMinutes { get; set; } = 15;
}
