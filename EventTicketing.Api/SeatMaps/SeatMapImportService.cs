using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.SeatMaps;

public sealed class SeatMapImportService(AppDbContext db, TimeProvider timeProvider)
{
    private const int BatchSize = 500;
    private const int MaximumSeatCount = 20_000;
    private const int MaximumRowLength = 50;
    private const int MaximumNumberLength = 50;
    private const int MaximumCategoryLength = 100;

    public async Task<SeatMapImportResult> ImportAsync(
        int performanceId,
        SeatMapImportDocument document,
        CancellationToken cancellationToken = default)
    {
        var seats = ValidateAndNormalize(performanceId, document);

        await using var transaction = await db.Database.BeginTransactionAsync(
            cancellationToken);

        // Imports for one performance must be serialized, including the first import where no seat row exists yet.
        var isPostgres = db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        if (isPostgres)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({performanceId})",
                cancellationToken);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existingSeats = isPostgres
            ? await db.Seats
                .FromSqlInterpolated($"SELECT * FROM seats WHERE performance_id = {performanceId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken)
            : await db.Seats
                .AsNoTracking()
                .Where(seat => seat.PerformanceId == performanceId)
                .ToListAsync(cancellationToken);
        var soldSeats = existingSeats.Count(seat => seat.Status == SeatStatus.Sold);
        var activeHolds = existingSeats.Count(seat =>
            seat.Status == SeatStatus.Held && seat.HeldUntil > now);

        if (soldSeats > 0 || activeHolds > 0)
        {
            throw new SeatMapReplacementBlockedException(soldSeats, activeHolds);
        }

        var replacedExistingMap = existingSeats.Count > 0;

        var categories = await db.SeatCategories
            .AsNoTracking()
            .Where(category => category.PerformanceId == performanceId)
            .ToDictionaryAsync(category => category.NormalizedName, StringComparer.Ordinal, cancellationToken);

        var requestedCategories = seats
            .GroupBy(seat => seat.NormalizedCategory, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Category, StringComparer.Ordinal);
        var createdCategoryCount = 0;

        await db.Seats
            .Where(seat => seat.PerformanceId == performanceId)
            .ExecuteDeleteAsync(cancellationToken);

        var obsoleteCategories = categories
            .Where(entry => !requestedCategories.ContainsKey(entry.Key))
            .Select(entry => entry.Value)
            .ToList();

        if (obsoleteCategories.Count > 0)
        {
            var obsoleteCategoryIds = obsoleteCategories.Select(category => category.Id).ToList();
            await db.SeatCategories
                .Where(category => obsoleteCategoryIds.Contains(category.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var category in obsoleteCategories)
        {
            categories.Remove(category.NormalizedName);
        }

        foreach (var (normalizedCategory, categoryName) in requestedCategories)
        {
            if (categories.ContainsKey(normalizedCategory))
            {
                continue;
            }

            var category = new SeatCategory
            {
                PerformanceId = performanceId,
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
            db.Seats.AddRange(batch.Select(item => new Seat
            {
                PerformanceId = performanceId,
                SeatCategoryId = categories[item.NormalizedCategory].Id,
                Row = item.Row,
                Number = item.Number,
                Status = SeatStatus.Available
            }));

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SeatMapImportResult(seats.Count, createdCategoryCount, replacedExistingMap);
    }

    private static List<NormalizedSeat> ValidateAndNormalize(int performanceId, SeatMapImportDocument document)
    {
        var errors = new List<string>();

        if (performanceId <= 0)
        {
            errors.Add("performanceId must be greater than zero.");
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
                category.ToUpperInvariant()));
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
        string NormalizedCategory);
}
