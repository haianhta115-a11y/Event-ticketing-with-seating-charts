using EventTicketing.Api.Security;
using System.Security.Claims;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Mvc;
using EventTicketing.Api.Data;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Endpoints;

public record HoldSeatRequest(int? UserId = null);
public record HoldSeatsRequest(int[]? SeatIds);

public static class SeatReservationEndpoints
{
    public static IEndpointRouteBuilder MapSeatReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/showtimes/{showtimeId:int}/seats")
                       .WithTags("Seat Reservations");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SeatDataUnresolvedException)
            {
                return Results.Json(new { errorCode = "SEAT_DATA_UNRESOLVED", message = "Dữ liệu ghế cần được đối soát trước khi thực hiện thao tác." }, statusCode: 409);
            }
            catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedTable)
            {
                return Results.Json(new { errorCode = "SEAT_SCHEMA_NOT_READY", message = "Sơ đồ ghế chưa sẵn sàng; cần hoàn tất chuyển đổi dữ liệu đã được duyệt." }, statusCode: 503);
            }
        });

        group.MapPost("/hold", async (int showtimeId, [FromBody] HoldSeatsRequest request,
            ISeatReservationService reservationService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = AccessPolicies.UserId(user);
            if (!userId.HasValue) return Results.Unauthorized();
            if (request.SeatIds is not { Length: > 0 and <= 100 } ids || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length)
                return Results.BadRequest(new { success = false, errorCode = "INVALID_SEAT_IDS", message = "Cần 1–100 mã ghế dương, không trùng nhau.", rejectedSeatIds = Array.Empty<int>() });
            var result = await reservationService.HoldSeatsAsync(showtimeId, ids, userId.Value, ct);
            if (result.IsSuccess)
                return Results.Ok(new
                {
                    success = true,
                    seatIds = ids,
                    status = "Held",
                    heldUntilUtc = result.HeldUntilUtc,
                    serverTimeUtc = result.ServerTimeUtc,
                    remainingSeconds = result.RemainingSeconds,
                    heldSeats = result.HeldSeats
                });
            var status = result.FailureReason switch
            {
                HoldFailureReason.NotFound => 404,
                HoldFailureReason.SalesClosed => 400,
                _ => 409
            };
            var code = result.FailureReason switch
            {
                HoldFailureReason.NotFound => "SEAT_NOT_FOUND",
                HoldFailureReason.SalesClosed => "SALES_CLOSED",
                HoldFailureReason.AlreadySold => "SEAT_ALREADY_SOLD",
                _ => "SEAT_ALREADY_HELD"
            };
            return Results.Json(new
            {
                success = false,
                errorCode = code,
                message = result.ErrorMessage,
                rejectedSeatIds = result.RejectedSeatIds,
                serverTimeUtc = result.ServerTimeUtc
            }, statusCode: status);
        }).RequireAuthorization(AccessPolicies.SeatBooking)
            .WithName("HoldSeatsAtomic").WithMetadata(new RequestSizeLimitAttribute(8192));

        // 1. Endpoint giữ chỗ 1 ghế (Seat Hold)
        group.MapPost("/{seatId:int}/hold", async (
            int showtimeId,
            int seatId,
            [FromBody] HoldSeatRequest? request,
            ISeatReservationService reservationService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = AccessPolicies.UserId(user);
            if (!userId.HasValue)
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Cần đăng nhập để giữ chỗ.",
                    errorCode = "USER_REQUIRED"
                }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await reservationService.HoldSeatAsync(showtimeId, seatId, userId.Value, ct);

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
        .RequireAuthorization(AccessPolicies.SeatBooking);

        // 2. Endpoint hủy giữ chỗ ghế (Release Seat Hold)
        group.MapDelete("/{seatId:int}/hold", async (
            int showtimeId,
            int seatId,
            [FromQuery] int? userIdQuery,
            ISeatReservationService reservationService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = AccessPolicies.UserId(user);
            if (!userId.HasValue)
            {
                return Results.Unauthorized();
            }

            var result = await reservationService.ReleaseSeatAsync(showtimeId, seatId, userId.Value, ct);
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
        .RequireAuthorization(AccessPolicies.SeatBooking);

        // 3. Endpoint lấy sơ đồ ghế với trạng thái thời gian thực
        group.MapGet("/", async (
            int showtimeId,
            [FromQuery] int? currentUserId,
            ISeatReservationService reservationService,
            AppDbContext db,
            TimeProvider timeProvider,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var resolvedUserId = AccessPolicies.UserId(user);
            var seats = await reservationService.GetSeatingChartStatusAsync(showtimeId, resolvedUserId, ct);
            // Preserve schema-not-ready 503 precedence on an incompatible legacy schema;
            // do not disguise a missing seat column as a missing showtime/empty map.
            if (!await db.Showtimes.AnyAsync(s => s.Id == showtimeId, ct))
                return Results.NotFound(new { message = "Suất diễn không tồn tại." });
            return Results.Ok(new
            {
                serverTimeUtc = timeProvider.GetUtcNow().UtcDateTime,
                hasSeatMap = seats.Count > 0,
                seats
            });
        })
        .WithName("Getshowtimeseats")
        .WithSummary("Lấy danh sách ghế và trạng thái thời gian thực của suất diễn")
        .Produces(StatusCodes.Status200OK)
        .AllowAnonymous();

        // 4. Endpoint lấy thông tin phiên giữ chỗ của user
        group.MapGet("/my-hold", async (
            int showtimeId,
            [FromQuery] int? currentUserId,
            ISeatReservationService reservationService,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var resolvedUserId = AccessPolicies.UserId(user);
            if (!resolvedUserId.HasValue)
            {
                return Results.Unauthorized();
            }

            var session = await reservationService.GetUserHoldSessionAsync(showtimeId, resolvedUserId.Value, ct);
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
        .RequireAuthorization(AccessPolicies.SeatBooking);

        return app;
    }

}
