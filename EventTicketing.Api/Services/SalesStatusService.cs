using System.Security.Claims;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public sealed class SalesStatusException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public IReadOnlyList<string> UnpricedCategories { get; }

    public SalesStatusException(int status, string code, string message, IReadOnlyList<string>? unpricedCategories = null)
        : base(message)
    {
        Status = status;
        Code = code;
        UnpricedCategories = unpricedCategories ?? Array.Empty<string>();
    }
}

public sealed class SalesStatusService(AppDbContext db, TimeProvider clock, IAuthorizationService authorization)
{
    private async Task<Event> LockEvent(int id, ClaimsPrincipal user, CancellationToken ct)
    {
        var evt = await db.Events.FromSqlInterpolated($"SELECT * FROM events WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new SalesStatusException(404, "EVENT_NOT_FOUND", "Không tìm thấy sự kiện.");
        if (!(await authorization.AuthorizeAsync(user, evt, AccessPolicies.EventOwner)).Succeeded)
            throw new SalesStatusException(403, "EVENT_FORBIDDEN", "Bạn không có quyền quản lý sự kiện này.");
        return evt;
    }

    private void Audit(Event evt, int? showId, string from, string to, ClaimsPrincipal user)
    {
        db.SalesTransitions.Add(new SalesTransition
        {
            EventId = evt.Id,
            ShowtimeId = showId,
            ActorUserId = AccessPolicies.UserId(user)
                ?? throw new SalesStatusException(401, "USER_REQUIRED", "Cần đăng nhập."),
            FromStatus = from,
            ToStatus = to,
            OccurredAtUtc = clock.GetUtcNow().UtcDateTime
        });
    }

    public async Task<EventStatus> SetPublishedAsync(int id, bool published, ClaimsPrincipal user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var evt = await LockEvent(id, user, ct);
        if (evt.Status == EventStatus.Ended)
            throw new SalesStatusException(409, "EVENT_ENDED", "Sự kiện đã kết thúc.");
        var next = published ? EventStatus.Published : EventStatus.Draft;
        if (evt.Status != next)
        {
            Audit(evt, null, evt.Status.ToString(), next.ToString(), user);
            evt.Status = next; evt.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return next;
    }

    public async Task<ShowtimeStatus> SetSalesAsync(int id, string? value, ClaimsPrincipal user, CancellationToken ct)
    {
        // Reject numeric enum inputs and attempts to reset an established showtime to Draft.
        var next = value switch
        {
            "OnSale" => ShowtimeStatus.OnSale,
            "Closed" => ShowtimeStatus.Closed,
            _ => throw new SalesStatusException(400, "INVALID_SALES_STATUS", "Trạng thái phải là OnSale hoặc Closed.")
        };
        var eventId = await db.Showtimes.Where(s => s.Id == id).Select(s => (int?)s.EventId).SingleOrDefaultAsync(ct)
            ?? throw new SalesStatusException(404, "SHOWTIME_NOT_FOUND", "Không tìm thấy suất diễn.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var evt = await LockEvent(eventId, user, ct);
        var show = await db.Showtimes.FromSqlInterpolated($"SELECT * FROM showtimes WHERE id={id} FOR UPDATE").SingleAsync(ct);
        if (next == ShowtimeStatus.OnSale)
        {
            if (evt.Status != EventStatus.Published)
                throw new SalesStatusException(409, "EVENT_NOT_PUBLISHED", "Cần xuất bản sự kiện trước khi mở bán.");
            // Same lock as seat-map import, including the first import with no rows yet.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({id})", ct);
            if (!await db.Seats.AnyAsync(s => s.ShowtimeId == id, ct))
                throw new SalesStatusException(409, "SEAT_MAP_REQUIRED", "Cần nạp sơ đồ ghế trước khi mở bán.");

            var unpricedCategories = await db.SeatCategories
                .Where(c => c.ShowtimeId == id && c.Price == null)
                .OrderBy(c => c.Id)
                .Select(c => c.Name)
                .Distinct()
                .ToListAsync(ct);

            if (unpricedCategories.Count > 0)
            {
                var unpricedListStr = string.Join(", ", unpricedCategories);
                throw new SalesStatusException(
                    409,
                    "SEAT_PRICING_REQUIRED",
                    $"Cần thiết lập giá vé cho các hạng ghế trước khi mở bán: {unpricedListStr}.",
                    unpricedCategories);
            }
        }
        if (show.Status == ShowtimeStatus.Draft && next == ShowtimeStatus.Closed)
            throw new SalesStatusException(409, "INVALID_SALES_TRANSITION", "Suất nháp chưa mở bán để đóng.");
        if (show.Status != next)
        {
            Audit(evt, id, show.Status.ToString(), next.ToString(), user);
            show.Status = next;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return next;
    }
}
