namespace EventTicketing.Api.Entities;

public class SeatCategory
{
    public int Id { get; set; }
    public int ShowtimeId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public decimal? Price { get; set; }

    public ICollection<Seat> Seats { get; set; } = new List<Seat>();

    public Showtime Showtime { get; set; } = null!;
}
