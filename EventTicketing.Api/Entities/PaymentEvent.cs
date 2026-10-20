namespace EventTicketing.Api.Entities;

public class PaymentEvent
{
    public int Id { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public int? OrderId { get; set; }
    public decimal? Amount { get; set; }
    public bool? Success { get; set; }
    public string RawPayload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Order? Order { get; set; }
}
