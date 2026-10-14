using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public sealed class ExpiredSeatHoldCleanup(AppDbContext db, IReservationClock clock)
{
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var now = await clock.NowAsync(db, ct);
        // Conditional UPDATE, never a stale ID list. PostgreSQL rechecks this predicate
        // after waiting for a concurrent replacement/renewal. No order model exists yet.
        return await db.Seats.Where(s => s.Status == SeatStatus.Held && s.HeldUntil <= now)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(s => s.Status, SeatStatus.Available)
                .SetProperty(s => s.HeldByUserId, (int?)null)
                .SetProperty(s => s.HeldUntil, (DateTime?)null), ct);
    }
}
