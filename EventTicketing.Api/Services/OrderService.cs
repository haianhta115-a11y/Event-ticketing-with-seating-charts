using System.Data;
using EventTicketing.Api.Configuration;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EventTicketing.Api.Services;

public sealed class OrderService(
    AppDbContext db,
    IReservationClock clock,
    IOptions<OrderOptions>? options = null,
    ILogger<OrderService>? logger = null) : IOrderService
{
    private readonly TimeSpan paymentTimeout = TimeSpan.FromMinutes((options?.Value ?? new OrderOptions()).PaymentTimeoutMinutes);

    public async Task<CreateOrderResult> CreateOrderFromHoldsAsync(
        int showtimeId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var isRelational = db.Database.IsRelational();
        await using var transaction = isRelational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        if (isRelational)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({showtimeId}, {userId})", cancellationToken);
        }

        var sampledNow = await clock.NowAsync(db, cancellationToken);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        // 1. Kiểm tra trạng thái mở bán của suất diễn (S-16 Deps S-07)
        var showtime = await db.Showtimes
            .Include(s => s.Event)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == showtimeId, cancellationToken);

        if (showtime == null)
        {
            return CreateOrderResult.Failed(CreateOrderFailureReason.ShowtimeNotFound, "Suất diễn không tồn tại.", now);
        }

        if (showtime.Status != ShowtimeStatus.OnSale || (showtime.Event != null && showtime.Event.Status != EventStatus.Published))
        {
            logger?.LogWarning("Order creation rejected: showtime {ShowtimeId} is not on sale", showtimeId);
            return CreateOrderResult.Failed(CreateOrderFailureReason.SalesClosed, "Suất diễn không còn mở bán.", now);
        }

        // 2. Chống bấm đúp / Idempotency: Kiểm tra nếu đã có đơn hàng PENDING còn hạn cho suất này
        // AC 3: "Giả sử tôi đã có một đơn đang chờ cho suất này, Khi bấm 'đặt vé' lần nữa, Thì được đưa về đơn đang chờ đó thay vì tạo đơn thứ hai"
        var existingPendingOrder = await db.Orders
            .Include(o => o.Showtime)
                .ThenInclude(s => s.Event)
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Seat)
                    .ThenInclude(s => s.SeatCategory)
            .FirstOrDefaultAsync(o => o.UserId == userId && o.ShowtimeId == showtimeId && o.Status == OrderStatus.Pending && o.ExpiresAt > now, cancellationToken);

        if (existingPendingOrder != null)
        {
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            logger?.LogInformation("User {UserId} already has active pending order {OrderId} for showtime {ShowtimeId}. Returning existing order.",
                userId, existingPendingOrder.Id, showtimeId);

            var events = await db.PaymentEvents
                .Where(pe => pe.OrderId == existingPendingOrder.Id)
                .OrderBy(pe => pe.CreatedAt)
                .Select(pe => new OrderPaymentEventDto(
                    pe.TransactionId,
                    pe.Amount ?? 0m,
                    pe.Success ?? false,
                    pe.CreatedAt
                ))
                .ToListAsync(cancellationToken);

            var hasFailedPayments = events.Any(pe => !pe.Success);
            var failedPaymentCount = events.Count(pe => !pe.Success);

            return CreateOrderResult.Success(
                MapToDto(existingPendingOrder, now, hasFailedPayments, failedPaymentCount, events),
                isExistingPendingOrder: true);
        }

        // 3. Đọc danh sách ghế đang giữ của user từ cơ sở dữ liệu
        // NFR: "không nhận danh sách ghế từ trình duyệt — đọc từ giữ chỗ trong cơ sở dữ liệu"
        var heldSeats = await db.Seats
            .Include(s => s.SeatCategory)
            .Where(s => s.ShowtimeId == showtimeId && s.HeldByUserId == userId && s.Status == SeatStatus.Held)
            .OrderBy(s => s.Row)
            .ThenBy(s => s.Number)
            .ToListAsync(cancellationToken);

        if (heldSeats.Count == 0)
        {
            return CreateOrderResult.Failed(CreateOrderFailureReason.NoSeatsHeld, "Không có ghế nào đang được giữ.", now);
        }

        // 4. Kiểm tra xem có ghế nào đã hết hạn giữ chỗ không
        // AC 2: "Giả sử một trong các giữ chỗ đã hết hạn, Khi bấm 'đặt vé', Thì không tạo đơn và báo ghế nào đã mất"
        var expiredSeats = heldSeats.Where(s => s.HeldUntil == null || s.HeldUntil <= now).ToList();
        if (expiredSeats.Count > 0)
        {
            var lostSeats = expiredSeats.Select(s => new LostSeatDto(s.Id, s.Row, s.Number)).ToList();
            logger?.LogWarning("Order creation rejected: {Count} seats expired for user {UserId}, showtime {ShowtimeId}",
                lostSeats.Count, userId, showtimeId);
            return CreateOrderResult.Failed(
                CreateOrderFailureReason.SeatHoldExpired,
                "Một hoặc nhiều ghế đã hết hạn giữ chỗ.",
                now,
                lostSeats);
        }

        // 5. Kiểm tra giá của từng hạng ghế (S-15)
        var unpricedSeats = heldSeats.Where(s => s.SeatCategory == null || s.SeatCategory.Price == null).ToList();
        if (unpricedSeats.Count > 0)
        {
            return CreateOrderResult.Failed(
                CreateOrderFailureReason.UnpricedCategory,
                "Một số hạng ghế chưa được thiết lập giá vé.",
                now);
        }

        // 6. Tính toán thời hạn thanh toán và tạo Order + OrderItems
        // NFR T-36: "giá trên dòng đơn là bản sao tại thời điểm đặt, không tham chiếu giá hiện tại"
        var expiresAt = now.Add(paymentTimeout);
        var orderItems = heldSeats.Select(s => new OrderItem
        {
            SeatId = s.Id,
            ShowtimeId = showtimeId,
            UnitPrice = s.SeatCategory.Price!.Value,
            Status = OrderStatus.Pending
        }).ToList();

        var totalAmount = OrderCalculator.CalculateTotal(orderItems);

        var order = new Order
        {
            UserId = userId,
            ShowtimeId = showtimeId,
            Status = OrderStatus.Pending,
            TotalAmount = totalAmount,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            OrderItems = orderItems
        };

        db.Orders.Add(order);

        // 7. Gia hạn thời hạn giữ chỗ của các ghế bằng thời hạn thanh toán
        // AC 1 & NFR: "thời hạn giữ chỗ được gia hạn bằng thời hạn thanh toán; tạo đơn và gia hạn giữ chỗ là một giao dịch"
        foreach (var seat in heldSeats)
        {
            seat.HeldUntil = expiresAt;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Xử lý race condition khi 2 request gửi đồng thời vượt qua advisory lock hoặc chèn cùng lúc
            logger?.LogInformation("Concurrent order creation caught by unique constraint for user {UserId}, showtime {ShowtimeId}", userId, showtimeId);
            var racedOrder = await db.Orders
                .Include(o => o.OrderItems)
                    .ThenInclude(oi => oi.Seat)
                        .ThenInclude(s => s.SeatCategory)
                .FirstOrDefaultAsync(o => o.UserId == userId && o.ShowtimeId == showtimeId && o.Status == OrderStatus.Pending, cancellationToken);

            if (racedOrder != null)
            {
                return CreateOrderResult.Success(MapToDto(racedOrder, now), isExistingPendingOrder: true);
            }
            throw;
        }

        order.Showtime = showtime;
        logger?.LogInformation("Order {OrderId} successfully created for user {UserId}, showtime {ShowtimeId}, seats count {Count}, total {Total}",
            order.Id, userId, showtimeId, orderItems.Count, totalAmount);

        return CreateOrderResult.Success(MapToDto(order, now), isExistingPendingOrder: false);
    }

    public async Task<OrderDto?> GetOrderByIdAsync(int orderId, int userId, CancellationToken cancellationToken = default)
    {
        var sampledNow = await clock.NowAsync(db, cancellationToken);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        var order = await db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Seat)
                    .ThenInclude(s => s.SeatCategory)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, cancellationToken);

        if (order == null) return null;

        if (order.Showtime == null && order.ShowtimeId > 0)
        {
            var st = await db.Showtimes
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == order.ShowtimeId, cancellationToken);
            if (st != null) order.Showtime = st;
        }

        var events = await db.PaymentEvents
            .Where(pe => pe.OrderId == order.Id)
            .OrderBy(pe => pe.CreatedAt)
            .Select(pe => new OrderPaymentEventDto(
                pe.TransactionId,
                pe.Amount ?? 0m,
                pe.Success ?? false,
                pe.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        var hasFailedPayments = events.Any(pe => !pe.Success);
        var failedPaymentCount = events.Count(pe => !pe.Success);

        return MapToDto(order, now, hasFailedPayments, failedPaymentCount, events);
    }

    public async Task<OrderDto?> GetPendingOrderForShowtimeAsync(int showtimeId, int userId, CancellationToken cancellationToken = default)
    {
        var sampledNow = await clock.NowAsync(db, cancellationToken);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        var order = await db.Orders
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Seat)
                    .ThenInclude(s => s.SeatCategory)
            .FirstOrDefaultAsync(o => o.UserId == userId && o.ShowtimeId == showtimeId && o.Status == OrderStatus.Pending && o.ExpiresAt > now, cancellationToken);

        if (order == null) return null;

        if (order.Showtime == null && order.ShowtimeId > 0)
        {
            var st = await db.Showtimes
                .Include(s => s.Event)
                .FirstOrDefaultAsync(s => s.Id == order.ShowtimeId, cancellationToken);
            if (st != null) order.Showtime = st;
        }

        var events = await db.PaymentEvents
            .Where(pe => pe.OrderId == order.Id)
            .OrderBy(pe => pe.CreatedAt)
            .Select(pe => new OrderPaymentEventDto(
                pe.TransactionId,
                pe.Amount ?? 0m,
                pe.Success ?? false,
                pe.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        var hasFailedPayments = events.Any(pe => !pe.Success);
        var failedPaymentCount = events.Count(pe => !pe.Success);

        return MapToDto(order, now, hasFailedPayments, failedPaymentCount, events);
    }

    private static OrderDto MapToDto(
        Order order,
        DateTime now,
        bool hasFailedPayments = false,
        int failedPaymentCount = 0,
        IReadOnlyList<OrderPaymentEventDto>? paymentEvents = null)
    {
        var isExpired = order.Status == OrderStatus.Expired || (order.Status == OrderStatus.Pending && order.ExpiresAt <= now);
        var remainingSeconds = Math.Max(0, (int)Math.Round((order.ExpiresAt - now).TotalSeconds));
        var items = order.OrderItems.Select(oi => new OrderItemDto(
            oi.Id,
            oi.SeatId,
            oi.Seat?.Row ?? "",
            oi.Seat?.Number ?? "",
            oi.Seat?.SeatCategory?.Name,
            oi.UnitPrice
        )).ToList();

        return new OrderDto(
            order.Id,
            order.UserId,
            order.ShowtimeId,
            order.Status.ToString(),
            order.TotalAmount,
            order.ExpiresAt,
            order.CreatedAt,
            now,
            remainingSeconds,
            items,
            hasFailedPayments,
            failedPaymentCount,
            paymentEvents ?? Array.Empty<OrderPaymentEventDto>(),
            order.Showtime?.Event?.Name,
            order.Showtime?.StartTime,
            order.Showtime?.EndTime,
            isExpired
        );
    }
}
