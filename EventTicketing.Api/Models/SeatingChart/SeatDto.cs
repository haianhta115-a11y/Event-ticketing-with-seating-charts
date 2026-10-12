namespace EventTicketing.Api.Models.SeatingChart;

public class SeatDto
{
    public string? SeatNumber { get; set; }
    public string? Row { get; set; }
    public string? Section { get; set; }
    public string? Category { get; set; }
    public decimal? Price { get; set; }
    public string? Status { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool? IsAccessible { get; set; }
}

