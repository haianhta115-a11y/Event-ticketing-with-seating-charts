using System.Security.Claims;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Api.Endpoints;

public record HoldSeatRequest(int? UserId = null);

public static class SeatReservationEndpoints
{
    public static IEndpointRouteBuilder MapSeatReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/performances/{performanceId:int}/seats")
                       .WithTags("Seat Reservations");

        // 1. Endpoint giữ chỗ 1 ghế (Seat Hold)
        group.MapPost("/{seatId:int}/hold", async (
            int performanceId,
            int seatId,
            [FromBody] HoldSeatRequest? request,
            ISeatReservationService reservationService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(request?.UserId, user);
            if (!userId.HasValue)
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Cần đăng nhập hoặc cung cấp userId để giữ chỗ.",
                    errorCode = "USER_REQUIRED"
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await reservationService.HoldSeatAsync(performanceId, seatId, userId.Value, ct);

            if (result.IsSuccess)
            {
                return Results.Ok(new
                {
                    success = true,
                    seatId = result.SeatId,
                    status = "Held",
                    heldUntilUtc = result.HeldUntilUtc,
                    serverTimeUtc = result.ServerTimeUtc,
                    remainingSeconds = result.RemainingSeconds,
                    heldSeats = result.HeldSeats
                });
            }

            return result.FailureReason switch
            {
                HoldFailureReason.SalesClosed => Results.Json(new
                {
                    success = false,
                    message = result.ErrorMessage,
                    errorCode = "SALES_CLOSED",
                    serverTimeUtc = result.ServerTimeUtc
                }, statusCode: StatusCodes.Status400BadRequest),

                HoldFailureReason.AlreadyHeld => Results.Json(new
                {
                    success = false,
                    message = result.ErrorMessage,
                    errorCode = "SEAT_ALREADY_HELD",
                    serverTimeUtc = result.ServerTimeUtc,
                    currentSeat = result.CurrentSeat != null
                        ? new { id = result.CurrentSeat.Id, status = result.CurrentSeat.Status.ToString() }
                        : null
                }, statusCode: StatusCodes.Status409Conflict),

                HoldFailureReason.AlreadySold => Results.Json(new
                {
                    success = false,
                    message = result.ErrorMessage,
                    errorCode = "SEAT_ALREADY_SOLD",
                    serverTimeUtc = result.ServerTimeUtc
                }, statusCode: StatusCodes.Status409Conflict),

                HoldFailureReason.NotFound => Results.Json(new
                {
                    success = false,
                    message = result.ErrorMessage,
                    errorCode = "SEAT_NOT_FOUND",
                    serverTimeUtc = result.ServerTimeUtc
                }, statusCode: StatusCodes.Status404NotFound),

                _ => Results.BadRequest(new { success = false, message = result.ErrorMessage })
            };
        })
        .WithName("HoldSeat")
        .WithSummary("Giữ chỗ một ghế trong 10 phút (chống race condition và đồng bộ thời hạn session)")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict)
        .AllowAnonymous();

        // 2. Endpoint hủy giữ chỗ ghế (Release Seat Hold)
        group.MapDelete("/{seatId:int}/hold", async (
            int performanceId,
            int seatId,
            [FromQuery] int? userIdQuery,
            ISeatReservationService reservationService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = ResolveUserId(userIdQuery, user);
            if (!userId.HasValue)
            {
                return Results.Unauthorized();
            }

            var result = await reservationService.ReleaseSeatAsync(performanceId, seatId, userId.Value, ct);
            if (result.IsSuccess)
            {
                return Results.Ok(new { success = true, message = "Đã hủy giữ chỗ thành công." });
            }

            if (result.FailureReason == ReleaseFailureReason.Forbidden)
            {
                return Results.Json(new { success = false, message = result.ErrorMessage }, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.NotFound(new { success = false, message = result.ErrorMessage });
        })
        .WithName("ReleaseSeatHold")
        .WithSummary("Hủy giữ chỗ ghế")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .AllowAnonymous();

        // 3. Endpoint lấy sơ đồ ghế với trạng thái thời gian thực
        group.MapGet("/", async (
            int performanceId,
            [FromQuery] int? currentUserId,
            ISeatReservationService reservationService,
            TimeProvider timeProvider,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var resolvedUserId = ResolveUserId(currentUserId, user);
            var seats = await reservationService.GetSeatingChartStatusAsync(performanceId, resolvedUserId, ct);
            return Results.Ok(new
            {
                serverTimeUtc = timeProvider.GetUtcNow().UtcDateTime,
                seats
            });
        })
        .WithName("GetPerformanceSeats")
        .WithSummary("Lấy danh sách ghế và trạng thái thời gian thực của suất diễn")
        .Produces(StatusCodes.Status200OK)
        .AllowAnonymous();

        // 4. Endpoint lấy thông tin phiên giữ chỗ của user
        group.MapGet("/my-hold", async (
            int performanceId,
            [FromQuery] int? currentUserId,
            ISeatReservationService reservationService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var resolvedUserId = ResolveUserId(currentUserId, user);
            if (!resolvedUserId.HasValue)
            {
                return Results.Unauthorized();
            }

            var session = await reservationService.GetUserHoldSessionAsync(performanceId, resolvedUserId.Value, ct);
            if (session == null)
            {
                return Results.NotFound(new { message = "Không có ghế nào đang được giữ." });
            }

            return Results.Ok(session);
        })
        .WithName("GetUserHoldSession")
        .WithSummary("Lấy thông tin phiên giữ chỗ hiện tại của người dùng")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .AllowAnonymous();

        return app;
    }

    private static int? ResolveUserId(int? explicitUserId, ClaimsPrincipal user)
    {
        if (explicitUserId.HasValue && explicitUserId.Value > 0)
        {
            return explicitUserId.Value;
        }

        var idClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(idClaim) && int.TryParse(idClaim, out var parsedId))
        {
            return parsedId;
        }

        return null;
    }
}
