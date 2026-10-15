using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Controllers;

public sealed record SalesStatusRequest(string? NewStatus);

[ApiController]
[Route("api")]
[Authorize(Policy = AccessPolicies.EventManagement)]
public sealed class SalesController(SalesStatusService sales) : ControllerBase
{
    private async Task<IActionResult> Transition(Func<Task<string>> action)
    {
        try { return Ok(new { status = await action() }); }
        catch (SalesStatusException error)
        {
            if (error.UnpricedCategories.Count > 0)
            {
                return StatusCode(error.Status, new
                {
                    errorCode = error.Code,
                    message = error.Message,
                    unpricedCategories = error.UnpricedCategories
                });
            }
            return StatusCode(error.Status, new { errorCode = error.Code, message = error.Message });
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn })
        {
            return StatusCode(503, new { errorCode = "SALES_SCHEMA_NOT_READY", message = "Schema mở bán chưa sẵn sàng; cần triển khai thay đổi đã được duyệt." });
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            return StatusCode(503, new { errorCode = "SALES_SCHEMA_NOT_READY", message = "Schema mở bán chưa sẵn sàng; cần triển khai thay đổi đã được duyệt." });
        }
    }

    [HttpPut("events/{id:int}/publish")]
    public Task<IActionResult> Publish(int id, CancellationToken ct) =>
        Transition(async () => (await sales.SetPublishedAsync(id, true, User, ct)).ToString());

    [HttpPut("events/{id:int}/unpublish")]
    public Task<IActionResult> Unpublish(int id, CancellationToken ct) =>
        Transition(async () => (await sales.SetPublishedAsync(id, false, User, ct)).ToString());

    [HttpPut("showtimes/{id:int}/status")]
    public Task<IActionResult> Status(int id, SalesStatusRequest request, CancellationToken ct) =>
        Transition(async () => (await sales.SetSalesAsync(id, request.NewStatus, User, ct)).ToString());
}
