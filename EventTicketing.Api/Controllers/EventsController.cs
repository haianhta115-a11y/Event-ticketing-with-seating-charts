using EventTicketing.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;

namespace EventTicketing.Api.Controllers
{
    public class CreateEventDto
    {
        [Required, StringLength(200)]
        public string Name { get; set; } = string.Empty;
        [Required]
        public string Description { get; set; } = string.Empty;
        [Required, StringLength(300)]
        public string Location { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
    }

    public class CreateShowtimeDto
    {
        [Required]
        public string StartTime { get; set; } = string.Empty;
        public string? EndTime { get; set; }
    }

    [ApiController]
    [Route("api/events")]
    public class EventsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IAuthorizationService _authorization;
        private readonly TimeProvider _clock;

        public EventsController(AppDbContext context, IAuthorizationService authorization, TimeProvider clock)
        {
            _context = context;
            _authorization = authorization;
            _clock = clock;
        }

        [HttpGet("admin")]
        [Authorize(Policy = AccessPolicies.EventManagement)]
        public async Task<IActionResult> GetAdminEvents()
        {
            // 1. Fetch cleanly with EF Core first
            var currentUserId = AccessPolicies.UserId(User);
            var events = await _context.Events
                .Where(e => User.IsInRole("Admin") || e.OwnerUserId == currentUserId)
                .Include(e => e.Showtimes)
                .OrderByDescending(e => e.Id)
                .ToListAsync();

            // 2. Map statuses in memory to prevent EF translation crashes
            var dtos = events.Select(e => new
            {
                e.Id,
                e.Name,
                e.Location,
                e.CreatedAt,
                ShowtimeCount = e.Showtimes.Count,
                Status = e.Status.ToString() // "Draft" or "Published"
            }).ToList();

            return Ok(dtos);
        }

        [HttpPost]
        [Authorize(Policy = AccessPolicies.EventManagement)]
        public async Task<IActionResult> CreateEvent([FromBody] CreateEventDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int currentUserId))
            {
                return Unauthorized(new { message = "Token không hợp lệ hoặc đã hết hạn." });
            }

            var newEvent = new Event
            {
                Name = dto.Name.Trim(),
                Description = dto.Description.Trim(),
                Location = dto.Location.Trim(),
                ImageUrl = dto.ImageUrl,
                Status = EventStatus.Draft, // Explicitly hardcode to Draft before saving
                OwnerUserId = currentUserId,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
                UpdatedAt = _clock.GetUtcNow().UtcDateTime
            };

            _context.Events.Add(newEvent);
            await _context.SaveChangesAsync();

            return Ok(new { id = newEvent.Id, message = "Tạo sự kiện thành công!" });
        }

        [HttpPut("{id:int}")]
        [Authorize(Policy = AccessPolicies.EventManagement)]
        public async Task<IActionResult> UpdateEvent(int id, CreateEventDto dto)
        {
            var evt = await _context.Events.FindAsync(id);
            if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện." });
            if (!(await _authorization.AuthorizeAsync(User, evt, AccessPolicies.EventOwner)).Succeeded) return Forbid();
            evt.Name = dto.Name.Trim();
            evt.Description = dto.Description.Trim();
            evt.Location = dto.Location.Trim();
            evt.ImageUrl = dto.ImageUrl;
            evt.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await _context.SaveChangesAsync();
            return Ok(new { id = evt.Id, message = "Cập nhật sự kiện thành công!" });
        }

        [HttpGet("{id:int}/showtimes")]
        [Authorize(Policy = AccessPolicies.EventManagement)]
        public async Task<IActionResult> GetShowtimes(int id)
        {
            var evt = await _context.Events.FindAsync(id);
            if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện." });
            if (!(await _authorization.AuthorizeAsync(User, evt, AccessPolicies.EventOwner)).Succeeded) return Forbid();
            try
            {
                var items = await _context.Showtimes.AsNoTracking().Where(s => s.EventId == id)
                    .OrderBy(s => s.StartTime).ThenBy(s => s.Id)
                    .Select(s => new
                    {
                        s.Id,
                        s.EventId,
                        s.StartTime,
                        s.EndTime,
                        s.Status,
                        HasSeatMap = _context.Seats.Any(seat => seat.ShowtimeId == s.Id)
                    }).ToListAsync();
                return Ok(items);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                // Do not substitute legacy relationships or hide a failed HasSeatMap query.
                return StatusCode(503, new { message = "Schema database chưa tương thích với phiên bản ứng dụng. Cần review migration trước khi tải danh sách suất diễn." });
            }
        }

        [HttpPost("{id:int}/showtimes")]
        [Authorize(Policy = AccessPolicies.EventManagement)]
        public async Task<IActionResult> CreateShowtime(int id, CreateShowtimeDto dto)
        {
            // Serialize creation for one event so a concurrent duplicate also receives a warning.
            await using var transaction = await _context.Database.BeginTransactionAsync();
            var evt = await _context.Events.FromSqlInterpolated($"SELECT * FROM events WHERE id = {id} FOR UPDATE")
                .SingleOrDefaultAsync();
            if (evt == null) return NotFound(new { message = "Không tìm thấy sự kiện." });
            if (!(await _authorization.AuthorizeAsync(User, evt, AccessPolicies.EventOwner)).Succeeded) return Forbid();
            if (!TryUtc(dto.StartTime, out var start)) ModelState.AddModelError("StartTime", "Thời điểm bắt đầu phải có timezone (Z hoặc offset).");
            else if (start <= _clock.GetUtcNow().UtcDateTime) ModelState.AddModelError("StartTime", "Thời điểm bắt đầu phải ở tương lai.");
            DateTime? end = null;
            if (dto.EndTime != null)
            {
                if (!TryUtc(dto.EndTime, out var parsedEnd)) ModelState.AddModelError("EndTime", "Thời điểm kết thúc phải có timezone (Z hoặc offset).");
                else { end = parsedEnd; if (end <= start) ModelState.AddModelError("EndTime", "Thời điểm kết thúc phải sau thời điểm bắt đầu."); }
            }
            if (!ModelState.IsValid) return ValidationProblem(ModelState);
            var overlap = await _context.Showtimes.AnyAsync(s => s.EventId == id &&
                (s.StartTime == start || (end != null && s.EndTime != null && s.StartTime < end && s.EndTime > start)));
            var showtime = new Showtime { EventId = id, StartTime = start, EndTime = end, Status = ShowtimeStatus.Draft };
            _context.Showtimes.Add(showtime);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
            return Ok(new
            {
                showtime.Id,
                showtime.EventId,
                showtime.StartTime,
                showtime.EndTime,
                showtime.Status,
                HasSeatMap = false,
                warnings = overlap ? new[] { "Trùng thời gian với một suất diễn khác. Có thể tổ chức song song ở hai phòng." } : Array.Empty<string>()
            });
        }

        private static bool TryUtc(string value, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)) return false;
            if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)) return false;
            utc = instant.UtcDateTime;
            return true;
        }

        [HttpGet("{id}")]
        [Authorize(Policy = AccessPolicies.EventManagement)]
        public async Task<IActionResult> GetEvent(int id)
        {
            // 1. Fetch cleanly with EF Core first
            var evt = await _context.Events.FindAsync(id);
            if (evt == null)
            {
                return NotFound(new { message = "Không tìm thấy sự kiện." });
            }

            // CRITICAL: Admins or the Owner can access the event
            if (!(await _authorization.AuthorizeAsync(User, evt, AccessPolicies.EventOwner)).Succeeded)
            {
                return StatusCode(403, new { message = "Bạn không có quyền truy cập sự kiện này." });
            }

            // 2. Map status in memory
            return Ok(new
            {
                evt.Id,
                evt.Name,
                evt.Description,
                evt.Location,
                evt.ImageUrl,
                Status = evt.Status.ToString(), // Converts correctly to String
                evt.OwnerUserId,
                evt.CreatedAt,
                evt.UpdatedAt
            });
        }
    }
}
