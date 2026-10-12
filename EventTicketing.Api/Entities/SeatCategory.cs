namespace EventTicketing.Api.Entities;

public class SeatCategory
{
    public int Id { get; set; }
    public int PerformanceId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }

    public ICollection<Seat> Seats { get; set; } = new List<Seat>();
}
