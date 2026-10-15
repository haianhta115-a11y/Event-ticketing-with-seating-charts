using EventTicketing.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using EventTicketing.Api.Data;
using EventTicketing.Api.Models.SeatingChart;
using EventTicketing.Api.Services;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Api.Models.Pricing;
using Npgsql;

namespace EventTicketing.Api.Controllers;

[ApiController]
[Route("api/showtimes")]
public class ShowtimesController(AppDbContext context, SeatingChartPreparation preparation,
    IAuthorizationService authorization, SeatMapImportService importer, ILogger<ShowtimesController> logger) : ControllerBase
{
    [HttpPost("{showtimeId:int}/seats/preview")]
    [Authorize(Policy = AccessPolicies.EventManagement)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> PreviewSeatingChart(int showtimeId, [FromForm] IFormFile file, CancellationToken ct)
    {
        var showtime = await context.Showtimes.Include(s => s.Event).FirstOrDefaultAsync(s => s.Id == showtimeId, ct);
        if (showtime == null) return NotFound(new { message = "Không tìm thấy suất diễn." });
        if (!(await authorization.AuthorizeAsync(User, showtime.Event, AccessPolicies.EventOwner)).Succeeded) return Forbid();
        var prepared = await preparation.ReadFileAsync(file, ct);
        if (!prepared.Validation.IsValid) return StatusCode(prepared.Validation.Errors.Any(e => e.ErrorCode == "FILE_TOO_LARGE") ? 413 : 400, prepared.Validation);
        return Ok(new { validation = prepared.Validation, seats = prepared.Document!.Seats, persisted = false });
    }
    [HttpPost("{showtimeId:int}/seats/import")]
    [Authorize(Policy = AccessPolicies.EventManagement)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> ImportSeatingChart(int showtimeId, [FromForm] IFormFile file, CancellationToken ct)
    {
        var showtime = await context.Showtimes.Include(s => s.Event).FirstOrDefaultAsync(s => s.Id == showtimeId, ct);
        if (showtime == null) return NotFound(new { message = "Không tìm thấy suất diễn." });
        if (!(await authorization.AuthorizeAsync(User, showtime.Event, AccessPolicies.EventOwner)).Succeeded) return Forbid();
        try
        {
            var prepared = await preparation.ReadFileAsync(file, ct);
            if (!prepared.Validation.IsValid) return StatusCode(prepared.Validation.Errors.Any(e => e.ErrorCode == "FILE_TOO_LARGE") ? 413 : 400, prepared.Validation);
            var result = await importer.ImportAsync(showtimeId, prepared.Document!, ct);
            return Ok(new
            {
                success = true,
                message = "Tải lên sơ đồ ghế thành công!",
                result.SeatCount,
                result.CreatedCategoryCount,
                result.ReplacedExistingMap
            });
        }
        catch (JsonException error)
        {
            return BadRequest(SeatingChartValidationResult.SingleError("JSON", error.Path ?? "root", "INVALID_JSON_SYNTAX",
                $"JSON không hợp lệ tại dòng {error.LineNumber}, cột {error.BytePositionInLine}."));
        }
        catch (SeatMapValidationException error) { return BadRequest(new { message = "Sơ đồ không hợp lệ.", errors = error.Errors }); }
        catch (SeatSchemaUnsupportedException)
        {
            return StatusCode(503, new { errorCode = "SEAT_SCHEMA_NOT_READY", message = "Sơ đồ ghế chưa hỗ trợ ghi dữ liệu an toàn; cần hoàn tất chuyển đổi đã được duyệt." });
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedTable)
        {
            return StatusCode(503, new { errorCode = "SEAT_SCHEMA_NOT_READY", message = "Sơ đồ ghế chưa hỗ trợ ghi dữ liệu an toàn; cần hoàn tất chuyển đổi đã được duyệt." });
        }
        catch (SeatDataUnresolvedException)
        {
            return Conflict(new { errorCode = "SEAT_DATA_UNRESOLVED", message = "Dữ liệu ghế cần được đối soát trước khi thực hiện thao tác." });
        }
        catch (SeatMapReplacementBlockedException error)
        {
            return Conflict(new { message = "Không thể thay sơ đồ khi có ghế đã bán hoặc đang được giữ.", error.SoldSeats, error.ActiveHolds });
        }
        catch (SeatMapReferencedRecordsException)
        {
            return Conflict(new { message = "Sơ đồ có bảng dữ liệu tham chiếu chưa được hỗ trợ; cần kiểm tra trước khi thay thế." });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogError("Seat map import failed for showtime {ShowtimeId}; persistence details withheld.", showtimeId);
            return StatusCode(500, new { message = "Không thể lưu sơ đồ; dữ liệu trước đó được giữ nguyên." });
        }
    }

    [HttpGet("{showtimeId:int}/pricing")]
    [HttpGet("{showtimeId:int}/seat-categories")]
    [HttpGet("{showtimeId:int}/seat-categories/pricing")]
    [Authorize(Policy = AccessPolicies.EventManagement)]
    public async Task<IActionResult> GetSeatPricing(int showtimeId, [FromServices] ISeatPricingService pricingService, CancellationToken ct)
    {
        try
        {
            var result = await pricingService.GetShowtimeSeatPricingAsync(showtimeId, User, ct);
            return Ok(result);
        }
        catch (SeatPricingException ex)
        {
            return StatusCode(ex.Status, new { errorCode = ex.Code, message = ex.Message });
        }
    }

    [HttpPut("{showtimeId:int}/pricing")]
    [HttpPut("{showtimeId:int}/seat-categories")]
    [HttpPut("{showtimeId:int}/seat-categories/pricing")]
    [Authorize(Policy = AccessPolicies.EventManagement)]
    public async Task<IActionResult> UpdateSeatPricing(int showtimeId, [FromBody] UpdateSeatPricingRequest request, [FromServices] ISeatPricingService pricingService, CancellationToken ct)
    {
        try
        {
            var result = await pricingService.UpdateShowtimeSeatPricingAsync(showtimeId, request, User, ct);
            return Ok(new
            {
                success = true,
                message = "Cập nhật giá vé theo hạng ghế thành công!",
                categories = result
            });
        }
        catch (SeatPricingException ex)
        {
            return StatusCode(ex.Status, new { errorCode = ex.Code, message = ex.Message });
        }
    }
}
