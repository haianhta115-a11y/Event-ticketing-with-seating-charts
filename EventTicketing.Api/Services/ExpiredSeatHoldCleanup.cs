using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public sealed class ExpiredSeatHoldCleanup(AppDbContext db, IReservationClock clock)
{
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var now = await clock.NowAsync(db, ct);
        // Conditional UPDATE, never a stale ID list. Exclude seats in active pending orders (S-12.4)
        return await db.Seats.Where(s => s.Status == SeatStatus.Held && s.HeldUntil <= now
            && !db.OrderItems.Any(oi => oi.SeatId == s.Id && oi.Order.Status == OrderStatus.Pending && oi.Order.ExpiresAt > now))
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(s => s.Status, SeatStatus.Available)
                .SetProperty(s => s.HeldByUserId, (int?)null)
                .SetProperty(s => s.HeldUntil, (DateTime?)null), ct);
    }
}
