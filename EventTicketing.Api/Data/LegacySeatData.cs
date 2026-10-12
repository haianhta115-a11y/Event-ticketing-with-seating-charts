using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Data;

// Reconciliation-only read projections: never tracked, never used as active entity keys.
// JSON extraction also supports the pre-expand schema without inventing missing columns/values.
public static class LegacySeatData
{
    public static IQueryable<LegacySeatRecord> ReadSeats(AppDbContext db) => db.Database.SqlQueryRaw<LegacySeatRecord>("""
        SELECT id AS "Id", seat_category_id AS "SeatCategoryId",
            (to_jsonb(s)->>'performance_id')::integer AS "PerformanceId",
            (to_jsonb(s)->>'showtime_id')::integer AS "ShowtimeId",
            (to_jsonb(s)->>'x')::integer AS "X", (to_jsonb(s)->>'y')::integer AS "Y"
        FROM public.seats s
        """);

    public static IQueryable<LegacyCategoryRecord> ReadCategories(AppDbContext db) => db.Database.SqlQueryRaw<LegacyCategoryRecord>("""
        SELECT id AS "Id", (to_jsonb(c)->>'performance_id')::integer AS "PerformanceId",
            (to_jsonb(c)->>'showtime_id')::integer AS "ShowtimeId"
        FROM public.seat_categories c
        """);

    public static async Task RejectUnresolvedSeatAsync(AppDbContext db, int seatId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        if (await ReadSeats(db).AnyAsync(s => s.Id == seatId && s.ShowtimeId == null, ct))
            throw new SeatDataUnresolvedException();
    }
}

public sealed class LegacySeatRecord
{
    public int Id { get; set; }
    public int SeatCategoryId { get; set; }
    public int? PerformanceId { get; set; }
    public int? ShowtimeId { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
}
public sealed class LegacyCategoryRecord
{
    public int Id { get; set; }
    public int? PerformanceId { get; set; }
    public int? ShowtimeId { get; set; }
}
public sealed class SeatDataUnresolvedException : Exception;
