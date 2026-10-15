using System.Security.Claims;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.Pricing;
using EventTicketing.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Api.Services;

public sealed class SeatPricingException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public interface ISeatPricingService
{
    Task<List<SeatCategoryPricingResponseDto>> GetEventSeatPricingAsync(int eventId, ClaimsPrincipal user, CancellationToken ct = default);
    Task<List<SeatCategoryPricingResponseDto>> UpdateEventSeatPricingAsync(int eventId, UpdateSeatPricingRequest request, ClaimsPrincipal user, CancellationToken ct = default);
    Task<List<SeatCategoryPricingResponseDto>> GetShowtimeSeatPricingAsync(int showtimeId, ClaimsPrincipal user, CancellationToken ct = default);
    Task<List<SeatCategoryPricingResponseDto>> UpdateShowtimeSeatPricingAsync(int showtimeId, UpdateSeatPricingRequest request, ClaimsPrincipal user, CancellationToken ct = default);
}

public sealed class SeatPricingService(
    AppDbContext db,
    IAuthorizationService authorization,
    TimeProvider clock) : ISeatPricingService
{
    private async Task<Event> AuthorizeEventAsync(int eventId, ClaimsPrincipal user, CancellationToken ct)
    {
        var evt = await db.Events.FindAsync([eventId], ct)
            ?? throw new SeatPricingException(404, "EVENT_NOT_FOUND", "Không tìm thấy sự kiện.");

        if (!(await authorization.AuthorizeAsync(user, evt, AccessPolicies.EventOwner)).Succeeded)
            throw new SeatPricingException(403, "EVENT_FORBIDDEN", "Bạn không có quyền quản lý sự kiện này.");

        return evt;
    }

    private async Task<Showtime> AuthorizeShowtimeAsync(int showtimeId, ClaimsPrincipal user, CancellationToken ct)
    {
        var showtime = await db.Showtimes
            .Include(s => s.Event)
            .FirstOrDefaultAsync(s => s.Id == showtimeId, ct)
            ?? throw new SeatPricingException(404, "SHOWTIME_NOT_FOUND", "Không tìm thấy suất diễn.");

        if (!(await authorization.AuthorizeAsync(user, showtime.Event, AccessPolicies.EventOwner)).Succeeded)
            throw new SeatPricingException(403, "EVENT_FORBIDDEN", "Bạn không có quyền quản lý sự kiện này.");

        return showtime;
    }

    private static void ValidatePricingItems(UpdateSeatPricingRequest? request)
    {
        if (request?.Categories == null || request.Categories.Count == 0)
            throw new SeatPricingException(400, "INVALID_PRICING_REQUEST", "Danh sách giá vé không được để trống.");

        foreach (var item in request.Categories)
        {
            if (item.CategoryId <= 0)
                throw new SeatPricingException(400, "INVALID_CATEGORY_ID", "Mã hạng ghế không hợp lệ.");

            if (item.Price < 0)
                throw new SeatPricingException(400, "INVALID_PRICE", "Giá vé phải lớn hơn hoặc bằng 0.");
        }
    }

    public async Task<List<SeatCategoryPricingResponseDto>> GetEventSeatPricingAsync(
        int eventId,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        await AuthorizeEventAsync(eventId, user, ct);

        var showtimeIds = await db.Showtimes
            .Where(s => s.EventId == eventId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var categories = await db.SeatCategories
            .Where(c => showtimeIds.Contains(c.ShowtimeId))
            .Include(c => c.Seats)
            .OrderBy(c => c.ShowtimeId)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        return categories.Select(c => new SeatCategoryPricingResponseDto
        {
            Id = c.Id,
            ShowtimeId = c.ShowtimeId,
            Name = c.Name,
            Price = c.Price,
            SeatCount = c.Seats.Count
        }).ToList();
    }

    public async Task<List<SeatCategoryPricingResponseDto>> UpdateEventSeatPricingAsync(
        int eventId,
        UpdateSeatPricingRequest request,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        ValidatePricingItems(request);

        var evt = await AuthorizeEventAsync(eventId, user, ct);

        if (evt.Status == EventStatus.Ended)
            throw new SeatPricingException(409, "EVENT_ENDED", "Không thể thay đổi giá vé cho sự kiện đã kết thúc.");

        var showtimeIds = await db.Showtimes
            .Where(s => s.EventId == eventId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var requestedCategoryIds = request.Categories.Select(c => c.CategoryId).Distinct().ToList();

        var categories = await db.SeatCategories
            .Where(c => showtimeIds.Contains(c.ShowtimeId) && requestedCategoryIds.Contains(c.Id))
            .Include(c => c.Seats)
            .ToListAsync(ct);

        var foundIds = categories.Select(c => c.Id).ToHashSet();
        var missingIds = requestedCategoryIds.Where(id => !foundIds.Contains(id)).ToList();
        if (missingIds.Count > 0)
            throw new SeatPricingException(404, "CATEGORY_NOT_FOUND",
                $"Không tìm thấy hạng ghế với mã {string.Join(", ", missingIds)} trong sự kiện này.");

        var priceMap = request.Categories
            .GroupBy(c => c.CategoryId)
            .ToDictionary(g => g.Key, g => g.Last().Price);

        foreach (var category in categories)
        {
            if (priceMap.TryGetValue(category.Id, out var newPrice))
            {
                category.Price = newPrice;
            }
        }

        evt.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return categories.Select(c => new SeatCategoryPricingResponseDto
        {
            Id = c.Id,
            ShowtimeId = c.ShowtimeId,
            Name = c.Name,
            Price = c.Price,
            SeatCount = c.Seats.Count
        }).OrderBy(c => c.ShowtimeId).ThenBy(c => c.Id).ToList();
    }

    public async Task<List<SeatCategoryPricingResponseDto>> GetShowtimeSeatPricingAsync(
        int showtimeId,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        await AuthorizeShowtimeAsync(showtimeId, user, ct);

        var categories = await db.SeatCategories
            .Where(c => c.ShowtimeId == showtimeId)
            .Include(c => c.Seats)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        return categories.Select(c => new SeatCategoryPricingResponseDto
        {
            Id = c.Id,
            ShowtimeId = c.ShowtimeId,
            Name = c.Name,
            Price = c.Price,
            SeatCount = c.Seats.Count
        }).ToList();
    }

    public async Task<List<SeatCategoryPricingResponseDto>> UpdateShowtimeSeatPricingAsync(
        int showtimeId,
        UpdateSeatPricingRequest request,
        ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        ValidatePricingItems(request);

        var showtime = await AuthorizeShowtimeAsync(showtimeId, user, ct);

        if (showtime.Event.Status == EventStatus.Ended)
            throw new SeatPricingException(409, "EVENT_ENDED", "Không thể thay đổi giá vé cho sự kiện đã kết thúc.");

        if (showtime.Status == ShowtimeStatus.Closed)
            throw new SeatPricingException(409, "SHOWTIME_CLOSED", "Không thể thay đổi giá vé cho suất diễn đã đóng.");

        var requestedCategoryIds = request.Categories.Select(c => c.CategoryId).Distinct().ToList();

        var categories = await db.SeatCategories
            .Where(c => c.ShowtimeId == showtimeId && requestedCategoryIds.Contains(c.Id))
            .Include(c => c.Seats)
            .ToListAsync(ct);

        var foundIds = categories.Select(c => c.Id).ToHashSet();
        var missingIds = requestedCategoryIds.Where(id => !foundIds.Contains(id)).ToList();
        if (missingIds.Count > 0)
            throw new SeatPricingException(404, "CATEGORY_NOT_FOUND",
                $"Không tìm thấy hạng ghế với mã {string.Join(", ", missingIds)} trong suất diễn này.");

        var priceMap = request.Categories
            .GroupBy(c => c.CategoryId)
            .ToDictionary(g => g.Key, g => g.Last().Price);

        foreach (var category in categories)
        {
            if (priceMap.TryGetValue(category.Id, out var newPrice))
            {
                category.Price = newPrice;
            }
        }

        showtime.Event.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return categories.Select(c => new SeatCategoryPricingResponseDto
        {
            Id = c.Id,
            ShowtimeId = c.ShowtimeId,
            Name = c.Name,
            Price = c.Price,
            SeatCount = c.Seats.Count
        }).OrderBy(c => c.Id).ToList();
    }
}
