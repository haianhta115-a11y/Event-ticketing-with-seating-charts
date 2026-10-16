using System.Security.Claims;
using EventTicketing.Api.Models.Orders;
using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize(Policy = AccessPolicies.SeatBooking)]
public sealed class OrdersController(IOrderService orderService) : ControllerBase
{
    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequest request, CancellationToken ct)
    {
        var userId = AccessPolicies.UserId(User);
        if (!userId.HasValue) return Unauthorized();

        if (!request.ShowtimeId.HasValue || request.ShowtimeId.Value <= 0)
        {
            return BadRequest(new
            {
                success = false,
                errorCode = "INVALID_SHOWTIME_ID",
                message = "ShowtimeId không hợp lệ."
            });
        }

        return await ProcessCreateOrder(request.ShowtimeId.Value, userId.Value, ct);
    }

    [HttpPost("showtimes/{showtimeId:int}/orders")]
    public async Task<IActionResult> CreateOrderForShowtime(int showtimeId, CancellationToken ct)
    {
        var userId = AccessPolicies.UserId(User);
        if (!userId.HasValue) return Unauthorized();

        return await ProcessCreateOrder(showtimeId, userId.Value, ct);
    }

    private async Task<IActionResult> ProcessCreateOrder(int showtimeId, int userId, CancellationToken ct)
    {
        var result = await orderService.CreateOrderFromHoldsAsync(showtimeId, userId, ct);

        if (result.IsSuccess)
        {
            if (result.IsExistingPendingOrder)
            {
                return Ok(new
                {
                    success = true,
                    message = "Đã có đơn hàng đang chờ thanh toán cho suất diễn này.",
                    order = result.Order
                });
            }

            return StatusCode(StatusCodes.Status201Created, new
            {
                success = true,
                message = "Tạo đơn hàng thành công.",
                order = result.Order
            });
        }

        return result.FailureReason switch
        {
            CreateOrderFailureReason.SeatHoldExpired => StatusCode(StatusCodes.Status409Conflict, new
            {
                success = false,
                errorCode = "SEAT_HOLD_EXPIRED",
                message = result.ErrorMessage,
                lostSeats = result.LostSeats,
                serverTimeUtc = result.ServerTimeUtc
            }),
            CreateOrderFailureReason.NoSeatsHeld => BadRequest(new
            {
                success = false,
                errorCode = "NO_SEATS_HELD",
                message = result.ErrorMessage
            }),
            CreateOrderFailureReason.SalesClosed => StatusCode(StatusCodes.Status409Conflict, new
            {
                success = false,
                errorCode = "SALES_CLOSED",
                message = result.ErrorMessage
            }),
            CreateOrderFailureReason.ShowtimeNotFound => NotFound(new
            {
                success = false,
                errorCode = "SHOWTIME_NOT_FOUND",
                message = result.ErrorMessage
            }),
            CreateOrderFailureReason.UnpricedCategory => BadRequest(new
            {
                success = false,
                errorCode = "UNPRICED_CATEGORY",
                message = result.ErrorMessage
            }),
            _ => BadRequest(new
            {
                success = false,
                message = result.ErrorMessage
            })
        };
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrder(int id, CancellationToken ct)
    {
        var userId = AccessPolicies.UserId(User);
        if (!userId.HasValue) return Unauthorized();

        var result = await orderService.GetOrderForUserAsync(id, userId.Value, ct);
        if (result.Status == GetOrderResultStatus.NotFound)
        {
            return NotFound(new
            {
                success = false,
                message = "Đơn hàng không tồn tại."
            });
        }

        if (result.Status == GetOrderResultStatus.Forbidden)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                success = false,
                message = "Bạn không có quyền xem đơn hàng này."
            });
        }

        return Ok(result.Order);
    }

    [HttpGet("showtimes/{showtimeId:int}/orders/pending")]
    public async Task<IActionResult> GetPendingOrder(int showtimeId, CancellationToken ct)
    {
        var userId = AccessPolicies.UserId(User);
        if (!userId.HasValue) return Unauthorized();

        var order = await orderService.GetPendingOrderForShowtimeAsync(showtimeId, userId.Value, ct);
        if (order == null)
        {
            return NotFound(new
            {
                success = false,
                message = "Không có đơn hàng nào đang chờ thanh toán cho suất diễn này."
            });
        }

        return Ok(order);
    }
}
