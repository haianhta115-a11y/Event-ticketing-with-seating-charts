namespace EventTicketing.Api.Entities;

public class Performance
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public bool IsSalesClosed { get; set; } = false;
}

