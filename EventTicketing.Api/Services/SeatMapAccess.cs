using System.Security.Claims;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public sealed class SeatMapAccessException(int status) : Exception("Suất diễn chưa mở bán.")
{
    public int Status { get; } = status;
}

public static class SeatMapAccess
{
    public static async Task<bool> RequireReadAsync(AppDbContext db, int showtimeId,
        int? ownerId, ClaimsPrincipal? viewer, DateTime now, CancellationToken ct)
    {
        var show = await db.Showtimes.AsNoTracking().Include(s => s.Event)
            .SingleOrDefaultAsync(s => s.Id == showtimeId, ct);
        if (show == null) throw new SeatMapAccessException(404);
        var publicMap = show.Status == ShowtimeStatus.OnSale && show.Event.Status == EventStatus.Published;
        if (publicMap) return true;
        // Management rights are evaluated from authenticated claims, never query/body IDs.
        if (viewer?.Identity?.IsAuthenticated == true &&
            (viewer.IsInRole("Admin") || viewer.IsInRole("Organizer") &&
                AccessPolicies.UserId(viewer) == show.Event.OwnerUserId)) return false;
        // Closing sales must not revoke an existing owner's recovery/release rights.
        if (ownerId.HasValue && (viewer == null || viewer.IsInRole("Buyer")) && await db.Seats.AnyAsync(s => s.ShowtimeId == showtimeId &&
            s.Status == SeatStatus.Held && s.HeldByUserId == ownerId && s.HeldUntil > now, ct)) return false;
        throw new SeatMapAccessException(403);
    }
}
