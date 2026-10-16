using EventTicketing.Api.Models.Orders;

namespace EventTicketing.Api.Services;

public interface IOrderService
{
    Task<CreateOrderResult> CreateOrderFromHoldsAsync(
        int showtimeId,
        int userId,
        CancellationToken cancellationToken = default);

    Task<OrderDto?> GetOrderByIdAsync(
        int orderId,
        int userId,
        CancellationToken cancellationToken = default);

    Task<GetOrderResult> GetOrderForUserAsync(
        int orderId,
        int userId,
        CancellationToken cancellationToken = default);

    Task<OrderDto?> GetPendingOrderForShowtimeAsync(
        int showtimeId,
        int userId,
        CancellationToken cancellationToken = default);
}
