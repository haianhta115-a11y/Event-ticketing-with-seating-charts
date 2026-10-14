using EventTicketing.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public interface IReservationClock
{
    Task<DateTime> NowAsync(AppDbContext db, CancellationToken ct = default);
}

// Production authority is PostgreSQL, not the web server's wall clock. Tests may
// explicitly replace this interface; no environment flag changes production time.
public sealed class PostgresReservationClock : IReservationClock
{
    public Task<DateTime> NowAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
}
