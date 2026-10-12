namespace EventTicketing.Api.Entities;

public class Seat
{
    public int Id { get; set; }
    public int PerformanceId { get; set; }
    public int SeatCategoryId { get; set; }
    public required string Row { get; set; }
    public required string Number { get; set; }
    public SeatStatus Status { get; set; } = SeatStatus.Available;
    public int? HeldByUserId { get; set; }
    public DateTime? HeldUntil { get; set; }

    public SeatCategory SeatCategory { get; set; } = null!;
}
