using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data;
using EventTicketing.Api.Configuration;
using Microsoft.Extensions.Options;

namespace EventTicketing.Api.Services;

public sealed class SeatReservationService(AppDbContext db, IReservationClock clock,
    IOptions<SeatHoldOptions>? options = null, ILogger<SeatReservationService>? logger = null) : ISeatReservationService
{
    private readonly TimeSpan holdDuration = TimeSpan.FromMinutes((options?.Value ?? new SeatHoldOptions()).DurationMinutes);
    public const string AlreadyHeldMessage = "ghế vừa có người chọn";

    public Task<HoldSeatResult> HoldSeatAsync(
        int ShowtimeId,
        int seatId,
        int userId,
        CancellationToken cancellationToken = default)
        => HoldSeatsAsync(ShowtimeId, [seatId], userId, cancellationToken);

    // One atomic operation; prior successful operations remain intact if this batch fails.
    public async Task<HoldSeatResult> HoldSeatsAsync(
        int ShowtimeId, IReadOnlyCollection<int> seatIds, int userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seatIds);
        if (seatIds.Count == 0) throw new ArgumentException("At least one seat is required.", nameof(seatIds));
        var orderedIds = seatIds.Distinct().Order().ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Database-scoped, transaction-owned lock also covers different API instances.
        // Two-int keys are disjoint from the importer's single-bigint advisory keys.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({ShowtimeId}, {userId})", cancellationToken);
        var sampledNow = await clock.NowAsync(db, cancellationToken);
        // PostgreSQL timestamps store microseconds. Return the exact persisted deadline,
        // not a 100ns remainder that disappears when S-14 reloads the hold from storage.
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        // AC 4: Kiểm tra suất diễn đã đóng bán chưa
        // Share locks serialize new holds against close/unpublish. Existing holds and
        // their release/read paths remain independent of the sales switch.
        var eventId = await db.Showtimes.Where(s => s.Id == ShowtimeId).Select(s => (int?)s.EventId)
            .SingleOrDefaultAsync(cancellationToken);
        var evt = eventId.HasValue ? await db.Events.FromSqlInterpolated(
            $"SELECT * FROM events WHERE id={eventId.Value} FOR SHARE").SingleOrDefaultAsync(cancellationToken) : null;
        var showtime = await db.Showtimes.FromSqlInterpolated(
            $"SELECT * FROM showtimes WHERE id={ShowtimeId} FOR SHARE")
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == ShowtimeId, cancellationToken);

        if (showtime != null && (showtime.Status != ShowtimeStatus.OnSale || evt?.Status != EventStatus.Published))
        {
            logger?.LogWarning("Seat hold rejected: {Reason}, showtime {ShowtimeId}, requested count {Count}", "SALES_CLOSED", ShowtimeId, orderedIds.Length);
            return HoldSeatResult.Failed("suất không còn mở bán", HoldFailureReason.SalesClosed, now, rejectedSeatIds: orderedIds);
        }

        var existingExpiry = await db.Seats.AsNoTracking()
            .Where(s => s.ShowtimeId == ShowtimeId && s.Status == SeatStatus.Held
                && s.HeldByUserId == userId && s.HeldUntil > now)
            .MinAsync(s => s.HeldUntil, cancellationToken);
        var targetHeldUntil = existingExpiry ?? now.Add(holdDuration);

        // Ascending row order prevents cycles between overlapping multi-seat batches.
        var rejected = new List<int>();
        HoldFailureReason? failure = null;
        string? failureMessage = null;
        Seat? firstRejectedSeat = null;
        foreach (var seatId in orderedIds)
        {
            var currentSeat = await db.Seats.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == seatId && s.ShowtimeId == ShowtimeId, cancellationToken);
            if (currentSeat == null)
            {
                await LegacySeatData.RejectUnresolvedSeatAsync(db, seatId, cancellationToken);
                rejected.Add(seatId);
                failure ??= HoldFailureReason.NotFound;
                failureMessage ??= "Ghế không tồn tại.";
                continue;
            }

            // PostgreSQL rechecks this predicate after waiting for a concurrent row writer.
            // Never use the earlier read as permission to overwrite a hold or a sale.
            var changed = await db.Seats
                .Where(s => s.Id == seatId && s.ShowtimeId == ShowtimeId
                    && (s.Status == SeatStatus.Available
                        || s.Status == SeatStatus.Held && (s.HeldUntil <= now || s.HeldByUserId == userId)))
                .ExecuteUpdateAsync(setter => setter
                    .SetProperty(s => s.Status, SeatStatus.Held)
                    .SetProperty(s => s.HeldByUserId, userId)
                    .SetProperty(s => s.HeldUntil, targetHeldUntil), cancellationToken);
            if (changed == 0)
            {
                currentSeat = await db.Seats.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == seatId && s.ShowtimeId == ShowtimeId, cancellationToken);
                if (currentSeat == null)
                {
                    rejected.Add(seatId); failure ??= HoldFailureReason.NotFound;
                    failureMessage ??= "Ghế không tồn tại.";
                    continue;
                }
                rejected.Add(seatId);
                failure ??= currentSeat.Status == SeatStatus.Sold ? HoldFailureReason.AlreadySold : HoldFailureReason.AlreadyHeld;
                failureMessage ??= currentSeat.Status == SeatStatus.Sold ? "Ghế đã được bán." : AlreadyHeldMessage;
                firstRejectedSeat ??= currentSeat;
            }
        }

        // Returning without COMMIT rolls back every provisional change, including seats
        // following a conflict. Collect all rejected IDs, never other holders' identities.
        if (rejected.Count > 0)
        {
            logger?.LogWarning("Seat hold rejected: {Reason}, showtime {ShowtimeId}, rejected count {Count}", failure.ToString(), ShowtimeId, rejected.Count);
            return HoldSeatResult.Failed(failureMessage!, failure!.Value, now, firstRejectedSeat, rejected);
        }

        // Giữ chỗ thành công! Lấy danh sách toàn bộ các ghế user đang giữ trong phiên này
        var myHeldSeats = await db.Seats
            .AsNoTracking()
            .Where(s => s.ShowtimeId == ShowtimeId
                     && s.HeldByUserId == userId
                     && s.Status == SeatStatus.Held
                     && s.HeldUntil > now)
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .Select(s => new HeldSeatDto(s.Id, s.Row, s.Number, s.SeatCategoryId, s.HeldUntil!.Value, s.SeatCategory.Price))
            .ToListAsync(cancellationToken);

        var remainingSeconds = Math.Max(0, (int)Math.Round((targetHeldUntil - now).TotalSeconds));
        await transaction.CommitAsync(cancellationToken);

        return HoldSeatResult.Success(
            orderedIds[0],
            targetHeldUntil,
            now,
            remainingSeconds,
            myHeldSeats);
    }

    public async Task<ReleaseSeatResult> ReleaseSeatAsync(
        int ShowtimeId,
        int seatId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({ShowtimeId}, {userId})", cancellationToken);
        var currentSeat = await db.Seats
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seatId && s.ShowtimeId == ShowtimeId, cancellationToken);

        if (currentSeat == null)
        {
            await LegacySeatData.RejectUnresolvedSeatAsync(db, seatId, cancellationToken);
            return ReleaseSeatResult.Failed(ReleaseFailureReason.NotFound, "Ghế không tồn tại.");
        }

        if (currentSeat.Status == SeatStatus.Held && currentSeat.HeldByUserId != null && currentSeat.HeldByUserId != userId)
        {
            return ReleaseSeatResult.Failed(ReleaseFailureReason.Forbidden, "Ghế đang được giữ bởi người khác.");
        }

        var rows = await db.Seats
            .Where(s => s.Id == seatId
                     && s.ShowtimeId == ShowtimeId
                     && s.Status == SeatStatus.Held
                     && s.HeldByUserId == userId)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(s => s.Status, SeatStatus.Available)
                .SetProperty(s => s.HeldByUserId, (int?)null)
                .SetProperty(s => s.HeldUntil, (DateTime?)null),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return rows > 0
            ? ReleaseSeatResult.Success()
            : ReleaseSeatResult.Failed(ReleaseFailureReason.NotFound, "Ghế không được giữ bởi bạn.");
    }

    public async Task<IReadOnlyList<SeatDisplayDto>> GetSeatingChartStatusAsync(
        int ShowtimeId,
        int? currentUserId = null,
        CancellationToken cancellationToken = default,
        System.Security.Claims.ClaimsPrincipal? viewer = null)
    {
        var now = await clock.NowAsync(db, cancellationToken);
        bool canHold;
        try { canHold = await SeatMapAccess.RequireReadAsync(db, ShowtimeId, currentUserId, viewer, now, cancellationToken); }
        catch (SeatMapAccessException error) when (error.Status == 404)
        {
            // Preserve legacy schema-not-ready precedence without reading any seat data.
            // A missing relationship column must not be disguised as a missing showtime.
            await db.Database.SqlQuery<int>($"SELECT showtime_id AS \"Value\" FROM seats WHERE FALSE").ToListAsync(cancellationToken);
            throw;
        }

        var seatsData = await db.Seats
            .AsNoTracking()
            .Where(s => s.ShowtimeId == ShowtimeId)
            .Select(s => new
            {
                s.Id,
                s.ShowtimeId,
                s.SeatCategoryId,
                Category = s.SeatCategory.Name,
                Price = s.SeatCategory.Price,
                s.Row,
                s.Number,
                s.X,
                s.Y,
                Status = (s.Status == SeatStatus.Sold || db.OrderItems.Any(oi => oi.SeatId == s.Id && oi.ShowtimeId == ShowtimeId && oi.Order.Status == OrderStatus.Paid))
                    ? SeatStatus.Sold
                    : (s.Status == SeatStatus.Held && s.HeldUntil <= now)
                        ? SeatStatus.Available
                        : s.Status,
                s.HeldUntil,
                s.HeldByUserId
            })
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .ToListAsync(cancellationToken);

        return seatsData.Select(s =>
        {
            string displayStatus;
            bool canSelect;

            if (s.Status == SeatStatus.Sold)
            {
                displayStatus = "Sold";
                canSelect = false;
            }
            else if (s.Status == SeatStatus.Held)
            {
                if (currentUserId.HasValue && s.HeldByUserId == currentUserId.Value)
                {
                    displayStatus = "HeldByMe";
                    canSelect = true;
                }
                else
                {
                    displayStatus = "HeldByOther";
                    canSelect = false;
                }
            }
            else
            {
                displayStatus = "Available";
                canSelect = canHold;
            }

            return new SeatDisplayDto(
                s.Id,
                s.ShowtimeId,
                s.SeatCategoryId,
                s.Row,
                s.Number,
                s.X,
                s.Y,
                displayStatus,
                s.Status == SeatStatus.Held ? s.HeldUntil : null,
                canSelect,
                s.Category,
                s.Price);
        }).ToList();
    }

    public async Task<UserHoldSessionDto?> GetUserHoldSessionAsync(
        int ShowtimeId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var now = await clock.NowAsync(db, cancellationToken);

        var myHeldSeats = await db.Seats
            .AsNoTracking()
            .Where(s => s.ShowtimeId == ShowtimeId
                     && s.HeldByUserId == userId
                     && s.Status == SeatStatus.Held
                     && s.HeldUntil > now)
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .Select(s => new HeldSeatDto(s.Id, s.Row, s.Number, s.SeatCategoryId, s.HeldUntil!.Value, s.SeatCategory.Price))
            .ToListAsync(cancellationToken);

        if (myHeldSeats.Count == 0)
        {
            return null;
        }

        var heldUntil = myHeldSeats[0].HeldUntilUtc;
        var remainingSeconds = Math.Max(0, (int)Math.Round((heldUntil - now).TotalSeconds));

        return new UserHoldSessionDto(
            ShowtimeId,
            userId,
            heldUntil,
            now,
            remainingSeconds,
            myHeldSeats);
    }
}

