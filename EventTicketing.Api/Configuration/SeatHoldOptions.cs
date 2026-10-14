namespace EventTicketing.Api.Configuration;

public sealed class SeatHoldOptions
{
    public const string SectionName = "SeatHolds";
    public int DurationMinutes { get; set; } = 10;
}
