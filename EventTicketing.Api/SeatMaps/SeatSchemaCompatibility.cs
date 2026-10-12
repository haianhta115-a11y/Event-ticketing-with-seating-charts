using EventTicketing.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EventTicketing.Api.SeatMaps;

public sealed class SeatSchemaUnsupportedException : Exception;

public static class SeatSchemaCompatibility
{
    public static async Task EnsureImportSupportedAsync(AppDbContext db, int showtimeId, bool hasUnknownCoordinates, CancellationToken ct)
    {
        // Keep schema stable from inspection through commit, before any row or sequence mutation.
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE public.seat_categories, public.seats IN ROW EXCLUSIVE MODE", ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = """
            SELECT (SELECT count(*) FROM information_schema.columns WHERE table_schema='public'
                AND (table_name='seats' AND column_name IN ('showtime_id','x','y')
                    OR table_name='seat_categories' AND column_name='showtime_id'))=4
              AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public'
                AND table_name IN ('seats','seat_categories') AND column_name='performance_id' AND is_nullable='NO')
              AND (NOT @unknown OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public'
                AND table_name='seats' AND column_name IN ('x','y') AND is_nullable='NO'))
            """;
        var unknown = command.CreateParameter(); unknown.ParameterName = "unknown"; unknown.Value = hasUnknownCoordinates;
        command.Parameters.Add(unknown);
        if (!(bool)(await command.ExecuteScalarAsync(ct))!) throw new SeatSchemaUnsupportedException();

        // Do not let filtered malformed rows disappear from S-05 replacement protection.
        var activeStep = db.Database.GetDbConnection().CreateCommand();
        await using (activeStep)
        {
            activeStep.Transaction = command.Transaction;
            activeStep.CommandText = """
                SELECT EXISTS (SELECT 1 FROM public.seats s LEFT JOIN public.seat_categories c
                    ON c.id=s.seat_category_id AND c.showtime_id=s.showtime_id
                    WHERE s.showtime_id=@showtime AND c.id IS NULL)
                """;
            var show = activeStep.CreateParameter(); show.ParameterName = "showtime"; show.Value = showtimeId;
            activeStep.Parameters.Add(show);
            if ((bool)(await activeStep.ExecuteScalarAsync(ct))!) throw new SeatDataUnresolvedException();
        }
    }
}
