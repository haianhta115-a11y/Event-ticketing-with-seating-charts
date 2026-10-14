namespace EventTicketing.Api.Entities;

// Persisted in the same transaction as the status change; no tokens or user payloads.
public sealed class SalesTransition
{
    public long Id { get; set; }
    public int EventId { get; set; }
    public int? ShowtimeId { get; set; }
    public int ActorUserId { get; set; }
    public required string FromStatus { get; set; }
    public required string ToStatus { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
