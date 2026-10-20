namespace EventTicketing.Api.Models.Orders;

public sealed record CreateOrderRequest(int? ShowtimeId = null);

public sealed record OrderItemDto(
    int Id,
    int SeatId,
    string Row,
    string Number,
    string? CategoryName,
    decimal UnitPrice);

public sealed record OrderPaymentEventDto(
    string TransactionId,
    decimal Amount,
    bool Success,
    DateTime CreatedAtUtc);

public sealed record OrderDto(
    int Id,
    int UserId,
    int ShowtimeId,
    string Status,
    decimal TotalAmount,
    DateTime ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime ServerTimeUtc,
    int RemainingSeconds,
    IReadOnlyList<OrderItemDto> Items,
    bool HasFailedPayments = false,
    int FailedPaymentCount = 0,
    IReadOnlyList<OrderPaymentEventDto>? PaymentEvents = null,
    string? EventName = null,
    DateTime? ShowtimeStartTimeUtc = null,
    DateTime? ShowtimeEndTimeUtc = null,
    bool IsExpired = false)
{
    public bool HasFailedPayment => HasFailedPayments;
}

public sealed record LostSeatDto(
    int SeatId,
    string Row,
    string Number);

public enum CreateOrderFailureReason
{
    NoSeatsHeld,
    SeatHoldExpired,
    SalesClosed,
    UnpricedCategory,
    ShowtimeNotFound,
    Conflict
}

public sealed class CreateOrderResult
{
    public bool IsSuccess { get; private init; }
    public bool IsExistingPendingOrder { get; private init; }
    public OrderDto? Order { get; private init; }
    public CreateOrderFailureReason? FailureReason { get; private init; }
    public string? ErrorMessage { get; private init; }
    public IReadOnlyList<LostSeatDto> LostSeats { get; private init; } = Array.Empty<LostSeatDto>();
    public DateTime ServerTimeUtc { get; private init; }

    public static CreateOrderResult Success(OrderDto order, bool isExistingPendingOrder = false) => new()
    {
        IsSuccess = true,
        IsExistingPendingOrder = isExistingPendingOrder,
        Order = order,
        ServerTimeUtc = order.ServerTimeUtc
    };

    public static CreateOrderResult Failed(
        CreateOrderFailureReason reason,
        string errorMessage,
        DateTime serverTimeUtc,
        IReadOnlyList<LostSeatDto>? lostSeats = null) => new()
        {
            IsSuccess = false,
            FailureReason = reason,
            ErrorMessage = errorMessage,
            LostSeats = lostSeats ?? Array.Empty<LostSeatDto>(),
            ServerTimeUtc = serverTimeUtc
        };
}
