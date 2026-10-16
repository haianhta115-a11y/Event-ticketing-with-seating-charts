namespace EventTicketing.Api.Entities;

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int ShowtimeId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public decimal TotalAmount { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    public decimal? PaidAmount { get; set; }
    public bool RequiresPaymentReview { get; set; } = false;
    public string? PaymentReviewReason { get; set; }

    public User User { get; set; } = null!;
    public Showtime Showtime { get; set; } = null!;
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
