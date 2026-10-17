using System.Data;
using System.Globalization;
using EventTicketing.Api.Configuration;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EventTicketing.Api.Services;

public sealed class PaymentService(
    AppDbContext db,
    IReservationClock clock,
    IOptions<PaymentOptions>? options = null,
    ILogger<PaymentService>? logger = null,
    IPaymentGateway? gateway = null) : IPaymentService
{
    private const int PaymentLockCategory = 2001;

    public async Task<InitiatePaymentResult> InitiatePaymentAsync(
        int orderId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var order = await db.Orders
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order == null || order.UserId != userId)
        {
            return new InitiatePaymentResult(
                InitiatePaymentOutcome.NotFound,
                ErrorMessage: "Đơn hàng không tồn tại hoặc không thuộc quyền sở hữu của bạn.");
        }

        if (order.Status != OrderStatus.Pending)
        {
            return new InitiatePaymentResult(
                InitiatePaymentOutcome.NotPayable,
                ErrorMessage: "Đơn hàng không ở trạng thái có thể thanh toán.");
        }

        var sampledNow = await clock.NowAsync(db, cancellationToken);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        if (order.ExpiresAt <= now)
        {
            return new InitiatePaymentResult(
                InitiatePaymentOutcome.Expired,
                ErrorMessage: "Đơn hàng đã hết hạn thanh toán.");
        }

        var orderCode = $"ORD-{order.Id}";
        var paymentOptions = options?.Value ?? new PaymentOptions();
        var effectiveGateway = gateway ?? (string.Equals(paymentOptions.Provider, "Sandbox", StringComparison.OrdinalIgnoreCase)
            ? new SandboxPaymentGateway(options)
            : new MockPaymentGateway(options));

        var redirectUrl = effectiveGateway.CreatePaymentUrl(order.Id, orderCode, order.TotalAmount, paymentOptions.ReturnUrl);

        var redirectDto = new PaymentRedirectDto(order.Id, orderCode, order.TotalAmount, redirectUrl);

        return new InitiatePaymentResult(
            InitiatePaymentOutcome.Ok,
            Data: redirectDto);
    }

    public async Task<ConfirmPaymentResult> ConfirmPaymentAsync(
        PaymentResultRequest request,
        CancellationToken cancellationToken = default)
    {
        var sampledNow = await clock.NowAsync(db, cancellationToken);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);
        return await ConfirmPaymentInternalAsync(request, null, now, cancellationToken);
    }

    public async Task<ProcessWebhookResult> ProcessWebhookAsync(
        WebhookPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            return new ProcessWebhookResult(
                Success: false,
                IsDuplicate: false,
                Outcome: ProcessWebhookOutcome.InvalidData,
                Message: "Mã giao dịch không hợp lệ.");
        }

        var sampledNow = await clock.NowAsync(db, cancellationToken);
        var now = new DateTime(sampledNow.Ticks - sampledNow.Ticks % 10, DateTimeKind.Utc);

        // 1. Kiểm tra nhanh mã giao dịch đã được ghi nhận trước đó hay chưa
        var existingEvent = await db.PaymentEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(pe => pe.TransactionId == request.TransactionId, cancellationToken);

        if (existingEvent != null)
        {
            logger?.LogInformation("Webhook with TransactionId {TransactionId} has already been processed. Returning 200 OK (idempotent ignore).", request.TransactionId);
            return new ProcessWebhookResult(
                Success: true,
                IsDuplicate: true,
                Outcome: ProcessWebhookOutcome.DuplicateIgnored,
                Message: "Webhook đã được xử lý trước đó. Bỏ qua yêu cầu trùng lặp.");
        }

        // 2. Mở giao dịch
        var isRelational = db.Database.IsRelational();
        await using var transaction = isRelational
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        var rawPayload = request.RawPayload ?? $"{request.OrderId}|{request.Amount.ToString(CultureInfo.InvariantCulture)}|{request.TransactionId}|{request.Success.ToString().ToLowerInvariant()}";

        var paymentEvent = new PaymentEvent
        {
            TransactionId = request.TransactionId,
            OrderId = request.OrderId,
            Amount = request.Amount,
            Success = request.Success,
            RawPayload = rawPayload,
            CreatedAt = now
        };

        db.PaymentEvents.Add(paymentEvent);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Trùng mã giao dịch do request đồng thời khác đã commit trước
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            logger?.LogWarning(ex, "Duplicate webhook detected by unique constraint for TransactionId {TransactionId}. Returning 200 OK without 500 error.", request.TransactionId);
            return new ProcessWebhookResult(
                Success: true,
                IsDuplicate: true,
                Outcome: ProcessWebhookOutcome.DuplicateIgnored,
                Message: "Webhook trùng lặp đã được tiếp nhận bởi tiến trình khác. Bỏ qua.");
        }

        // 3. Chèn thành công thì gọi T-42 trong cùng giao dịch
        var confirmResult = await ConfirmPaymentInternalAsync(
            new PaymentResultRequest(request.OrderId, request.TransactionId, request.Amount, request.Success),
            transaction,
            now,
            cancellationToken);

        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        if (confirmResult.Outcome == ConfirmPaymentOutcome.OrderNotFound)
        {
            return new ProcessWebhookResult(
                Success: false,
                IsDuplicate: false,
                Outcome: ProcessWebhookOutcome.OrderNotFound,
                PaymentResult: confirmResult,
                Message: confirmResult.Message);
        }

        var isSuccess = confirmResult.Outcome is ConfirmPaymentOutcome.Paid
            or ConfirmPaymentOutcome.AlreadyPaid
            or ConfirmPaymentOutcome.NeedsReview;

        return new ProcessWebhookResult(
            Success: isSuccess,
            IsDuplicate: false,
            Outcome: ProcessWebhookOutcome.Processed,
            PaymentResult: confirmResult,
            Message: confirmResult.Message);
    }

    private async Task<ConfirmPaymentResult> ConfirmPaymentInternalAsync(
        PaymentResultRequest request,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? existingTransaction,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var isRelational = db.Database.IsRelational();
        var shouldManageTransaction = isRelational && existingTransaction == null && db.Database.CurrentTransaction == null;
        await using var transaction = shouldManageTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        if (isRelational)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({PaymentLockCategory}, {request.OrderId})", cancellationToken);
        }

        var order = await db.Orders
            .IgnoreQueryFilters()
            .Include(o => o.OrderItems)
                .ThenInclude(oi => oi.Seat)
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);

        // 1. Không có đơn -> OrderNotFound
        if (order == null)
        {
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.OrderNotFound, "Đơn hàng không tồn tại.");
        }

        // 2. Đã Paid -> AlreadyPaid (idempotent)
        if (order.Status == OrderStatus.Paid)
        {
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            logger?.LogInformation("Order {OrderId} is already paid. Returning AlreadyPaid.", order.Id);
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.AlreadyPaid, "Đơn hàng đã được thanh toán trước đó.");
        }

        // 3. Success = false -> GatewayFailed
        if (!request.Success)
        {
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            logger?.LogWarning("Payment gateway reported failure for order {OrderId}, transaction {TransactionId}",
                order.Id, request.TransactionId);
            return new ConfirmPaymentResult(ConfirmPaymentOutcome.GatewayFailed, "Thanh toán tại cổng không thành công.");
        }

        // 4. Amount != TotalAmount -> lưu cờ RequiresPaymentReview, log Error, trả AmountMismatch
        if (request.Amount != order.TotalAmount)
        {
            order.RequiresPaymentReview = true;
            order.PaymentReviewReason = $"Số tiền thanh toán ({request.Amount.ToString(CultureInfo.InvariantCulture)}) không khớp với tổng đơn ({order.TotalAmount.ToString(CultureInfo.InvariantCulture)}).";
            order.PaidAmount = request.Amount;
            order.PaymentReference = request.TransactionId;
            order.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            logger?.LogError("Payment amount mismatch for order {OrderId}: expected {ExpectedAmount}, received {ReceivedAmount}, transaction {TransactionId}",
                order.Id, order.TotalAmount, request.Amount, request.TransactionId);

            return new ConfirmPaymentResult(ConfirmPaymentOutcome.AmountMismatch, "Số tiền thanh toán không khớp với tổng đơn.");
        }

        // 5. Đơn không Pending, hết hạn/huỷ, hoặc có ghế không còn Held bởi đúng user -> đánh dấu review, log Error, trả NeedsReview
        var isExpired = order.Status == OrderStatus.Expired ||
                        order.Status == OrderStatus.Cancelled ||
                        (order.Status == OrderStatus.Pending && order.ExpiresAt <= now);

        var invalidSeat = order.OrderItems.Any(oi =>
            oi.Seat == null ||
            oi.Seat.Status != SeatStatus.Held ||
            oi.Seat.HeldByUserId != order.UserId);

        if (isExpired || order.Status != OrderStatus.Pending || invalidSeat)
        {
            order.RequiresPaymentReview = true;
            if (isExpired)
            {
                order.PaymentReviewReason = "Đơn hàng đã bị huỷ hoặc hết hạn thanh toán. Cần kiểm tra để hoàn tiền.";
                if (order.Status == OrderStatus.Pending)
                {
                    order.Status = OrderStatus.Expired;
                }
            }
            else
            {
                order.PaymentReviewReason = "Đơn hàng không ở trạng thái Pending hoặc ghế không còn được giữ bởi người dùng.";
            }
            order.PaidAmount = request.Amount;
            order.PaymentReference = request.TransactionId;
            order.UpdatedAt = now;

            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            logger?.LogError("Payment requires review for order {OrderId}: status is {Status}, isExpired: {IsExpired}. Transaction {TransactionId}",
                order.Id, order.Status, isExpired, request.TransactionId);

            return new ConfirmPaymentResult(ConfirmPaymentOutcome.NeedsReview, "Đơn hàng cần kiểm tra do đã hết hạn hoặc trạng thái ghế không hợp lệ.");
        }

        // 6. Hợp lệ -> Order.Status=Paid, PaidAt, PaidAmount, PaymentReference; OrderItem.Status=Paid; Seat.Status=Sold, HeldByUserId=null, HeldUntil=null
        order.Status = OrderStatus.Paid;
        order.PaidAt = now;
        order.PaidAmount = request.Amount;
        order.PaymentReference = request.TransactionId;
        order.UpdatedAt = now;

        foreach (var item in order.OrderItems)
        {
            item.Status = OrderStatus.Paid;
            if (item.Seat != null)
            {
                item.Seat.Status = SeatStatus.Sold;
                item.Seat.HeldByUserId = null;
                item.Seat.HeldUntil = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger?.LogInformation("Order {OrderId} successfully paid. Transaction {TransactionId}, Amount {Amount}",
            order.Id, request.TransactionId, request.Amount);

        return new ConfirmPaymentResult(ConfirmPaymentOutcome.Paid, "Thanh toán thành công.");
    }
}
