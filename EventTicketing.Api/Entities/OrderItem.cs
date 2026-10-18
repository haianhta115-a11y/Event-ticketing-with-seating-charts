namespace EventTicketing.Api.Entities;

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ShowtimeId { get; set; }
    public int SeatId { get; set; }
    public decimal UnitPrice { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    public Order Order { get; set; } = null!;
    public Showtime Showtime { get; set; } = null!;
    public Seat Seat { get; set; } = null!;
}
