using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text;
using System.Data;

namespace EventTicketing.Api.SeatMaps;

public sealed class SeatMapImportService(AppDbContext db, TimeProvider timeProvider)
{
    private const int BatchSize = 500;
    private const int MaximumSeatCount = 20_000;
    private const int MaximumRowLength = 50;
    private const int MaximumNumberLength = 50;
    private const int MaximumCategoryLength = 100;

    public async Task<SeatMapImportResult> ImportAsync(
        int ShowtimeId,
        SeatMapImportDocument document,
        CancellationToken cancellationToken = default)
    {
        var seats = ValidateAndNormalize(ShowtimeId, document);

        await using var transaction = await db.Database.BeginTransactionAsync(
            cancellationToken);

        // Imports for one Showtime must be serialized, including the first import where no seat row exists yet.
        var isPostgres = db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        if (isPostgres)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({ShowtimeId})",
                cancellationToken);
            await SeatSchemaCompatibility.EnsureImportSupportedAsync(db, ShowtimeId,
                seats.Any(s => s.X == null || s.Y == null), cancellationToken);
        }

        var existingSeats = isPostgres
            ? await db.Seats
                .FromSqlInterpolated($"SELECT * FROM seats WHERE showtime_id = {ShowtimeId} ORDER BY id FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken)
            : await db.Seats
                .AsNoTracking()
                .Where(seat => seat.ShowtimeId == ShowtimeId)
                .ToListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var soldSeats = existingSeats.Count(seat => seat.Status == SeatStatus.Sold);
        var activeHolds = existingSeats.Count(seat =>
            seat.Status == SeatStatus.Held && seat.HeldUntil > now);

        if (soldSeats > 0 || activeHolds > 0)
        {
            throw new SeatMapReplacementBlockedException(soldSeats, activeHolds);
        }

        var replacedExistingMap = existingSeats.Count > 0;

        // Fail closed if tables outside the current model reference seats/categories. In particular,
        // an unknown ON DELETE CASCADE must never silently remove order or history records.
        if (isPostgres && (replacedExistingMap || await db.SeatCategories.AnyAsync(c => c.ShowtimeId == ShowtimeId, cancellationToken)))
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = """
                SELECT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE contype = 'f' AND confrelid IN ('seats'::regclass, 'seat_categories'::regclass)
                    AND NOT (conrelid = 'seats'::regclass AND confrelid = 'seat_categories'::regclass))
                """;
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                throw new SeatMapReferencedRecordsException();
        }

        var categories = await db.SeatCategories
            .AsNoTracking()
            .Where(category => category.ShowtimeId == ShowtimeId)
            .ToDictionaryAsync(category => category.NormalizedName, StringComparer.Ordinal, cancellationToken);

        var requestedCategories = seats
            .GroupBy(seat => seat.NormalizedCategory, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Category, StringComparer.Ordinal);
        var createdCategoryCount = 0;

        var requestedKeys = seats.Select(s => SeatKey(s.Row, s.Number)).ToHashSet(StringComparer.Ordinal);
        var existingByKey = existingSeats.ToDictionary(s => SeatKey(s.Row, s.Number), StringComparer.Ordinal);
        var obsoleteIds = existingSeats.Where(s => !requestedKeys.Contains(SeatKey(s.Row, s.Number))).Select(s => s.Id).ToList();
        if (obsoleteIds.Count > 0)
            await db.Seats.Where(s => s.ShowtimeId == ShowtimeId && obsoleteIds.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);

        var obsoleteCategories = categories
            .Where(entry => !requestedCategories.ContainsKey(entry.Key))
            .Select(entry => entry.Value)
            .ToList();

        foreach (var (normalizedCategory, categoryName) in requestedCategories)
        {
            if (categories.ContainsKey(normalizedCategory))
            {
                continue;
            }

            var category = new SeatCategory
            {
                ShowtimeId = ShowtimeId,
                Name = categoryName,
                NormalizedName = normalizedCategory
            };

            categories.Add(normalizedCategory, category);
            db.SeatCategories.Add(category);
            createdCategoryCount++;
        }

        // Save category changes inside the outer transaction; any later seat failure rolls them back too.
        await db.SaveChangesAsync(cancellationToken);

        foreach (var batch in seats.Chunk(BatchSize))
        {
            var inserts = new List<Seat>();
            foreach (var item in batch)
            {
                Seat seat;
                if (existingByKey.TryGetValue(SeatKey(item.Row, item.Number), out var snapshot))
                {
                    seat = db.Seats.Local.FirstOrDefault(s => s.Id == snapshot.Id) ?? snapshot;
                    if (db.Entry(seat).State == EntityState.Detached) db.Seats.Attach(seat);
                    else db.Entry(seat).CurrentValues.SetValues(snapshot);
                }
                else { seat = new Seat { ShowtimeId = ShowtimeId, Row = item.Row, Number = item.Number }; inserts.Add(seat); }
                seat.SeatCategoryId = categories[item.NormalizedCategory].Id;
                seat.Row = item.Row; seat.Number = item.Number;
                seat.X = item.X; seat.Y = item.Y;
                seat.Status = SeatStatus.Available;
                seat.HeldByUserId = null; seat.HeldUntil = null;
            }
            // Save tracked updates before a parameterized multi-row INSERT for new seats.
            await db.SaveChangesAsync(cancellationToken);
            if (inserts.Count > 0)
            {
                var sql = new StringBuilder("INSERT INTO seats (showtime_id, seat_category_id, \"row\", \"number\", x, y, status) VALUES ");
                var values = new List<object>();
                using var parameterFactory = db.Database.GetDbConnection().CreateCommand();
                object CoordinateParameter(string name, int? value)
                {
                    var parameter = parameterFactory.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.DbType = DbType.Int32;
                    parameter.Value = (object?)value ?? DBNull.Value;
                    return parameter;
                }
                foreach (var seat in inserts)
                {
                    if (values.Count > 0) sql.Append(',');
                    var offset = values.Count;
                    sql.Append('(').Append(string.Join(',', Enumerable.Range(offset, 7).Select(i => "{" + i + "}"))).Append(')');
                    values.AddRange([seat.ShowtimeId, seat.SeatCategoryId, seat.Row, seat.Number,
                        CoordinateParameter($"coordinate_x_{offset}", seat.X), CoordinateParameter($"coordinate_y_{offset}", seat.Y), "AVAILABLE"]);
                }
                await db.Database.ExecuteSqlRawAsync(sql.ToString(), values, cancellationToken);
            }
        }

        // Matching seats have now moved to their requested categories; delete only unused categories.
        if (obsoleteCategories.Count > 0)
        {
            var ids = obsoleteCategories.Select(c => c.Id).ToList();
            await db.SeatCategories.Where(c => c.ShowtimeId == ShowtimeId && ids.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SeatMapImportResult(seats.Count, createdCategoryCount, replacedExistingMap);
    }

    private static List<NormalizedSeat> ValidateAndNormalize(int ShowtimeId, SeatMapImportDocument document)
    {
        var errors = new List<string>();

        if (ShowtimeId <= 0)
        {
            errors.Add("ShowtimeId must be greater than zero.");
        }

        if (document.Seats is not { Count: > 0 })
        {
            errors.Add("seats must contain at least one seat.");
            throw new SeatMapValidationException(errors);
        }

        if (document.Seats.Count > MaximumSeatCount)
        {
            errors.Add($"seats cannot contain more than {MaximumSeatCount} seats.");
        }

        var normalizedSeats = new List<NormalizedSeat>(document.Seats.Count);
        var seatKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < document.Seats.Count; index++)
        {
            var input = document.Seats[index];
            if (input is null)
            {
                errors.Add($"seats[{index}] cannot be null.");
                continue;
            }

            var row = input.Row?.Trim() ?? string.Empty;
            var number = input.Number?.Trim() ?? string.Empty;
            var category = input.Category?.Trim() ?? string.Empty;
            var location = $"seats[{index}]";

            ValidateRequiredLength(row, MaximumRowLength, $"{location}.row", errors);
            ValidateRequiredLength(number, MaximumNumberLength, $"{location}.number", errors);
            ValidateRequiredLength(category, MaximumCategoryLength, $"{location}.category", errors);
            if (input.X < 0 || input.Y < 0) errors.Add($"{location} coordinates must be non-negative integers.");
            if (input.X.HasValue != input.Y.HasValue) errors.Add($"{location} coordinates must both be supplied or both be unknown.");

            if (row.Length == 0 || number.Length == 0 || category.Length == 0)
            {
                continue;
            }

            var normalizedRow = row.ToUpperInvariant();
            var normalizedNumber = number.ToUpperInvariant();
            var seatKey = $"{normalizedRow}\u001f{normalizedNumber}";

            if (!seatKeys.Add(seatKey))
            {
                errors.Add($"{location} duplicates seat {row}-{number}.");
                continue;
            }

            normalizedSeats.Add(new NormalizedSeat(
                row,
                number,
                category,
                category.ToUpperInvariant(), input.X, input.Y));
        }

        if (errors.Count > 0)
        {
            throw new SeatMapValidationException(errors);
        }

        return normalizedSeats;
    }

    private static void ValidateRequiredLength(
        string value,
        int maximumLength,
        string field,
        ICollection<string> errors)
    {
        if (value.Length == 0)
        {
            errors.Add($"{field} is required.");
        }
        else if (value.Length > maximumLength)
        {
            errors.Add($"{field} cannot exceed {maximumLength} characters.");
        }
    }

    private sealed record NormalizedSeat(
        string Row,
        string Number,
        string Category,
        string NormalizedCategory, int? X, int? Y);

    private static string SeatKey(string row, string number) => $"{row.Trim().ToUpperInvariant()}\u001f{number.Trim().ToUpperInvariant()}";
}
