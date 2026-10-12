namespace EventTicketing.Api.SeatMaps;

public sealed class SeatMapValidationException(IReadOnlyList<string> errors)
    : Exception("The seat map is invalid.")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class SeatMapReplacementBlockedException(int soldSeats, int activeHolds)
    : Exception("The seat map cannot be replaced because seats have been sold or are actively held.")
{
    public int SoldSeats { get; } = soldSeats;
    public int ActiveHolds { get; } = activeHolds;
}
