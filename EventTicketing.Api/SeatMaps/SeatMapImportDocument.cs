namespace EventTicketing.Api.SeatMaps;

public sealed class SeatMapImportDocument
{
    public List<SeatMapImportItem?>? Seats { get; init; }
}

public sealed class SeatMapImportItem
{
    public string? Row { get; init; }
    public string? Number { get; init; }
    public string? Category { get; init; }
}
