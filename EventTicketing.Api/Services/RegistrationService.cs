using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using EventTicketing.Api.Data;
using EventTicketing.Api.Email;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using StackExchange.Redis;
using EventTicketing.Api.Configuration;

namespace EventTicketing.Api.Services;

public sealed class RegistrationService(AppDbContext db, IEmailDelivery email, EmailDeliveryOptions options,
    IConnectionMultiplexer redis, TimeProvider clock, ILogger<RegistrationService> logger, RedisKeyScope? scope = null)
{
    public const string GenericMessage = "Nếu email hợp lệ, bạn sẽ nhận được hướng dẫn.";
    public static bool ValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static Dictionary<string, string[]> Validate(RegisterRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ValidEmail(request.Email)) errors["email"] = ["Email không hợp lệ."];
        if (string.IsNullOrWhiteSpace(request.Username)) errors["username"] = ["Username là bắt buộc."];
        if (string.IsNullOrWhiteSpace(request.FullName)) errors["fullName"] = ["Họ tên là bắt buộc."];
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 8)
            errors["password"] = ["Password phải có ít nhất 8 ký tự."];
        return errors;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var normalizedUsername = request.Username.Trim().ToLowerInvariant();
        // Hash even for duplicates, avoiding a cheap duplicate-only response path.
        var passwordHash = PasswordHasher.HashPassword(request.Password);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize registration collisions (including username/email aliases) before lookup.
        foreach (var identity in new[] { normalizedEmail, normalizedUsername }.Distinct().Order(StringComparer.Ordinal))
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", ct);
        if (await db.Users.AnyAsync(u => u.Email.ToLower() == normalizedEmail ||
            u.Email.ToLower() == normalizedUsername || (u.Username != null &&
            (u.Username.ToLower() == normalizedUsername || u.Username.ToLower() == normalizedEmail)), ct))
            return;

        var buyer = await db.Roles.SingleOrDefaultAsync(r => r.Name == "Buyer", ct)
            ?? throw new RegistrationUnavailableException();
        var token = NewToken();
        var now = Now;
        var user = new User
        {
            Email = normalizedEmail,
            Username = request.Username.Trim(),
            Name = request.FullName.Trim(),
            PasswordHash = passwordHash,
            Status = "Inactive",
            CreatedAt = now,
            UpdatedAt = now
        };
        user.UserRoles.Add(new UserRole { RoleId = buyer.Id });
        db.AccountActivations.Add(new AccountActivation { User = user, TokenHash = Hash(token), ExpiresAtUtc = now.AddHours(24) });
        try
        {
            // EF persists user, role assignment and activation together inside the transaction.
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(ct);
            return; // Same response for concurrent duplicates; no account-existence disclosure.
        }
        await DeliverAsync(normalizedEmail, token, ct);
    }

    public async Task<bool> ActivateAsync(string? token, CancellationToken ct)
    {
        if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)) return false;
        var hash = Hash(token);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Same row lock as resend: only one activation/rotation can win.
        var record = await db.AccountActivations.FromSqlInterpolated(
            $"SELECT * FROM account_activations WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync(ct);
        var now = Now;
        if (record == null || record.UsedAtUtc != null || record.ExpiresAtUtc <= now) return false;
        var user = await db.Users.SingleAsync(u => u.Id == record.UserId, ct);
        if (user.Status != "Inactive") return false;
        record.UsedAtUtc = now;
        user.Status = "Active";
        user.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<int> ResendAsync(string recipient, CancellationToken ct)
    {
        var normalizedEmail = recipient.Trim().ToLowerInvariant();
        var key = (RedisKey)(scope?.Key($"activation:resend:{Hash(normalizedEmail)}") ?? $"activation:resend:{Hash(normalizedEmail)}");
        var limited = await redis.GetDatabase().ScriptEvaluateAsync("""
            local time = redis.call('TIME')
            local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
            redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 3600000)
            if redis.call('ZCARD', KEYS[1]) >= 5 then
                local first = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
                return math.max(1, math.ceil((tonumber(first[2]) + 3600000 - now) / 1000))
            end
            redis.call('ZADD', KEYS[1], now, ARGV[1])
            redis.call('EXPIRE', KEYS[1], 3600)
            return 0
            """, [key], [Guid.NewGuid().ToString("N")]);
        if ((int)limited > 0) return (int)limited;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (user == null) return 0;
        var record = await db.AccountActivations.FromSqlInterpolated(
            $"SELECT * FROM account_activations WHERE \"UserId\" = {user.Id} FOR UPDATE").SingleOrDefaultAsync(ct);
        // Recheck user after waiting for the activation row lock.
        var status = await db.Users.Where(u => u.Id == user.Id).Select(u => u.Status).SingleAsync(ct);
        if (status != "Inactive" || record == null || record.UsedAtUtc != null) return 0;
        var token = NewToken();
        record.TokenHash = Hash(token);
        record.ExpiresAtUtc = Now.AddHours(24);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await DeliverAsync(normalizedEmail, token, ct);
        return 0;
    }

    private async Task DeliverAsync(string recipient, string token, CancellationToken ct)
    {
        // Fragment stays in the browser and is not sent to the frontend web server/access log.
        var link = options.ActivationUrl + "#token=" + Uri.EscapeDataString(token);
        try
        {
            await email.SendAsync(new EmailMessage(recipient, "Kích hoạt tài khoản", $"Xác nhận tài khoản: {link}\nLiên kết có hiệu lực 24 giờ và chỉ dùng một lần."), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not log recipient, link, token, credentials or provider exception text.
            logger.LogError("Activation email delivery failed; account remains inactive and resend is available.");
        }
    }
}

public sealed class RegistrationUnavailableException : Exception;
