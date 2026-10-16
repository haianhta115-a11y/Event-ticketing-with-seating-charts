namespace EventTicketing.Api.Models.Orders;

public sealed record CreateOrderRequest(int? ShowtimeId = null, decimal? TotalAmount = null);

public sealed record OrderItemDto(
    int Id,
    int SeatId,
    string Row,
    string Number,
    string? CategoryName,
    decimal UnitPrice);

public sealed record OrderDto(
    int Id,
    int UserId,
    int ShowtimeId,
    string? EventName,
    DateTime? ShowtimeStartTimeUtc,
    DateTime? ShowtimeEndTimeUtc,
    string Status,
    decimal TotalAmount,
    DateTime ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime ServerTimeUtc,
    int RemainingSeconds,
    bool IsExpired,
    IReadOnlyList<OrderItemDto> Items)
{
    public OrderDto(
        int id,
        int userId,
        int showtimeId,
        string status,
        decimal totalAmount,
        DateTime expiresAtUtc,
        DateTime createdAtUtc,
        DateTime serverTimeUtc,
        int remainingSeconds,
        IReadOnlyList<OrderItemDto> items)
        : this(
            id,
            userId,
            showtimeId,
            null,
            null,
            null,
            status,
            totalAmount,
            expiresAtUtc,
            createdAtUtc,
            serverTimeUtc,
            remainingSeconds,
            status.Equals("Expired", StringComparison.OrdinalIgnoreCase) || remainingSeconds <= 0,
            items)
    {
    }
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

public enum GetOrderResultStatus
{
    Success,
    NotFound,
    Forbidden
}

public sealed record GetOrderResult(GetOrderResultStatus Status, OrderDto? Order = null)
{
    public static GetOrderResult Success(OrderDto order) => new(GetOrderResultStatus.Success, order);
    public static GetOrderResult NotFound() => new(GetOrderResultStatus.NotFound);
    public static GetOrderResult Forbidden() => new(GetOrderResultStatus.Forbidden);
}
