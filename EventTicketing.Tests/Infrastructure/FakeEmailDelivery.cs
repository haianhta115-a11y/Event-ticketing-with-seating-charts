using System.Collections.Concurrent;
using EventTicketing.Api.Email;

namespace EventTicketing.Tests.Infrastructure;

internal sealed class FakeEmailDelivery : IEmailDelivery
{
    public ConcurrentQueue<EmailMessage> Messages { get; } = new();
    public bool FailDelivery { get; set; }
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailDelivery) throw new InvalidOperationException("Simulated delivery failure.");
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

internal sealed class TestClock : TimeProvider, EventTicketing.Api.Services.IReservationClock
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    public override DateTimeOffset GetUtcNow() => Now;
    public Task<DateTime> NowAsync(EventTicketing.Api.Data.AppDbContext db, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Now.UtcDateTime);
    }
}
