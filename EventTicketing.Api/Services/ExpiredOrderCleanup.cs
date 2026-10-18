using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Api.Services;

/// <summary>
/// S-23 / T-52: Quét và hủy các đơn hàng Pending đã quá hạn thanh toán, nhả ghế về kho trong cùng một giao dịch.
/// </summary>
public sealed class ExpiredOrderCleanup(
    AppDbContext db,
    IReservationClock clock,
    ILogger<ExpiredOrderCleanup>? logger = null)
{
    private const int OrderLockCategory = 2002;

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var sampledNow = await clock.NowAsync(db, ct);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        var isRelational = db.Database.IsRelational();
        await using var transaction = isRelational
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct)
            : null;

        // Tìm tất cả các đơn hàng đang Pending và đã quá hạn thanh toán
        var expiredOrders = await db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Seat)
            .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
            .ToListAsync(ct);

        if (expiredOrders.Count == 0)
        {
            if (transaction != null)
            {
                await transaction.CommitAsync(ct);
            }
            return 0;
        }

        var cancelledCount = 0;

        foreach (var order in expiredOrders)
        {
            // Nếu dùng relational database, áp dụng advisory lock cho từng order để chống tranh chấp với webhook (T-52)
            if (isRelational)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({OrderLockCategory}, {order.Id})", ct);
            }

            // Kiểm tra lại trạng thái để phòng trường hợp vừa được webhook thanh toán thành công
            if (order.Status != OrderStatus.Pending)
            {
                continue;
            }

            // Chuyển trạng thái đơn sang Expired
            order.Status = OrderStatus.Expired;
            order.UpdatedAt = now;
            cancelledCount++;

            // Nhả các ghế trong đơn hàng về Available nếu ghế chưa bị bán
            foreach (var item in order.OrderItems)
            {
                item.Status = OrderStatus.Expired;

                if (item.Seat != null && item.Seat.Status != SeatStatus.Sold)
                {
                    item.Seat.Status = SeatStatus.Available;
                    item.Seat.HeldByUserId = null;
                    item.Seat.HeldUntil = null;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        if (transaction != null)
        {
            await transaction.CommitAsync(ct);
        }

        logger?.LogInformation("Đã hủy {Count} đơn hàng quá hạn thanh toán và nhả ghế về kho lúc {Time}", cancelledCount, now);

        return cancelledCount;
    }
}
