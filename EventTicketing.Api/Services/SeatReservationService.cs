using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public sealed class SeatReservationService(AppDbContext db, TimeProvider timeProvider) : ISeatReservationService
{
    private static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromMinutes(10);

    public async Task<HoldSeatResult> HoldSeatAsync(
        int performanceId,
        int seatId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // AC 4: Kiểm tra suất diễn đã đóng bán chưa
        var performance = await db.Performances
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == performanceId, cancellationToken);

        if (performance != null && performance.IsSalesClosed)
        {
            return HoldSeatResult.Failed("suất không còn mở bán", HoldFailureReason.SalesClosed, now);
        }

        // AC 3: Kiểm tra phiên giữ chỗ hiện tại của user trong suất diễn này
        // Nếu user đã có ghế đang giữ (chưa hết hạn), dùng chung 1 mốc thời hạn tính từ ghế đầu tiên!
        var existingHoldUntil = await db.Seats
            .AsNoTracking()
            .Where(s => s.PerformanceId == performanceId
                     && s.HeldByUserId == userId
                     && s.Status == SeatStatus.Held
                     && s.HeldUntil > now)
            .OrderBy(s => s.HeldUntil)
            .Select(s => s.HeldUntil)
            .FirstOrDefaultAsync(cancellationToken);

        var targetHeldUntil = existingHoldUntil ?? now.Add(DefaultHoldDuration);

        // AC 1 & Concurrency: Thực thi Atomic Conditional Update để chống Race Condition
        // Một ghế chỉ được cập nhật nếu:
        // 1. Trạng thái là AVAILABLE
        // 2. Hoặc trạng thái là HELD nhưng đã hết hạn (HeldUntil <= now)
        // 3. Hoặc chính user này đang giữ ghế đó
        var rowsUpdated = await db.Seats
            .Where(s => s.Id == seatId
                     && s.PerformanceId == performanceId
                     && (s.Status == SeatStatus.Available
                         || s.HeldUntil <= now
                         || (s.Status == SeatStatus.Held && s.HeldByUserId == userId)))
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(s => s.Status, SeatStatus.Held)
                .SetProperty(s => s.HeldByUserId, userId)
                .SetProperty(s => s.HeldUntil, targetHeldUntil),
                cancellationToken);

        if (rowsUpdated > 0)
        {
            // Giữ chỗ thành công! Lấy danh sách toàn bộ các ghế user đang giữ trong phiên này
            var myHeldSeats = await db.Seats
                .AsNoTracking()
                .Where(s => s.PerformanceId == performanceId
                         && s.HeldByUserId == userId
                         && s.Status == SeatStatus.Held
                         && s.HeldUntil > now)
                .OrderBy(s => s.Row)
                .ThenBy(s => s.Number)
                .Select(s => new HeldSeatDto(s.Id, s.Row, s.Number, s.SeatCategoryId, s.HeldUntil!.Value))
                .ToListAsync(cancellationToken);

            var remainingSeconds = Math.Max(0, (int)Math.Round((targetHeldUntil - now).TotalSeconds));

            return HoldSeatResult.Success(
                seatId,
                targetHeldUntil,
                now,
                remainingSeconds,
                myHeldSeats);
        }

        // AC 2: Không cập nhật được dòng nào -> Ghế đã bị người khác chọn hoặc đã bán
        var currentSeat = await db.Seats
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seatId && s.PerformanceId == performanceId, cancellationToken);

        if (currentSeat == null)
        {
            return HoldSeatResult.Failed("Ghế không tồn tại.", HoldFailureReason.NotFound, now);
        }

        if (currentSeat.Status == SeatStatus.Sold)
        {
            return HoldSeatResult.Failed("Ghế đã được bán.", HoldFailureReason.AlreadySold, now, currentSeat);
        }

        return HoldSeatResult.Failed("ghế vừa có người chọn", HoldFailureReason.AlreadyHeld, now, currentSeat);
    }

    public async Task<ReleaseSeatResult> ReleaseSeatAsync(
        int performanceId,
        int seatId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var currentSeat = await db.Seats
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seatId && s.PerformanceId == performanceId, cancellationToken);

        if (currentSeat == null)
        {
            return ReleaseSeatResult.Failed(ReleaseFailureReason.NotFound, "Ghế không tồn tại.");
        }

        if (currentSeat.Status == SeatStatus.Held && currentSeat.HeldByUserId != null && currentSeat.HeldByUserId != userId)
        {
            return ReleaseSeatResult.Failed(ReleaseFailureReason.Forbidden, "Ghế đang được giữ bởi người khác.");
        }

        var rows = await db.Seats
            .Where(s => s.Id == seatId
                     && s.PerformanceId == performanceId
                     && s.Status == SeatStatus.Held
                     && s.HeldByUserId == userId)
            .ExecuteUpdateAsync(setter => setter
                .SetProperty(s => s.Status, SeatStatus.Available)
                .SetProperty(s => s.HeldByUserId, (int?)null)
                .SetProperty(s => s.HeldUntil, (DateTime?)null),
                cancellationToken);

        return rows > 0
            ? ReleaseSeatResult.Success()
            : ReleaseSeatResult.Failed(ReleaseFailureReason.NotFound, "Ghế không được giữ bởi bạn.");
    }

    public async Task<IReadOnlyList<SeatDisplayDto>> GetSeatingChartStatusAsync(
        int performanceId,
        int? currentUserId = null,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var seats = await db.Seats
            .AsNoTracking()
            .Where(s => s.PerformanceId == performanceId)
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .ToListAsync(cancellationToken);

        return seats.Select(s =>
        {
            var isHoldActive = s.Status == SeatStatus.Held && s.HeldUntil > now;
            string displayStatus;
            bool canSelect;

            if (s.Status == SeatStatus.Sold)
            {
                displayStatus = "Sold";
                canSelect = false;
            }
            else if (isHoldActive)
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
                canSelect = true;
            }

            return new SeatDisplayDto(
                s.Id,
                s.PerformanceId,
                s.SeatCategoryId,
                s.Row,
                s.Number,
                displayStatus,
                isHoldActive ? s.HeldUntil : null,
                canSelect);
        }).ToList();
    }

    public async Task<UserHoldSessionDto?> GetUserHoldSessionAsync(
        int performanceId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var myHeldSeats = await db.Seats
            .AsNoTracking()
            .Where(s => s.PerformanceId == performanceId
                     && s.HeldByUserId == userId
                     && s.Status == SeatStatus.Held
                     && s.HeldUntil > now)
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .Select(s => new HeldSeatDto(s.Id, s.Row, s.Number, s.SeatCategoryId, s.HeldUntil!.Value))
            .ToListAsync(cancellationToken);

        if (myHeldSeats.Count == 0)
        {
            return null;
        }

        var heldUntil = myHeldSeats[0].HeldUntilUtc;
        var remainingSeconds = Math.Max(0, (int)Math.Round((heldUntil - now).TotalSeconds));

        return new UserHoldSessionDto(
            performanceId,
            userId,
            heldUntil,
            now,
            remainingSeconds,
            myHeldSeats);
    }
}

