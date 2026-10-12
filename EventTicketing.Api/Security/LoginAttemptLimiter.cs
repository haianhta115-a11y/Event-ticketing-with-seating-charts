using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace EventTicketing.Api.Security;

// Redis owns both counters and TTLs, so lockouts survive application restarts.
public sealed class LoginAttemptLimiter(IConnectionMultiplexer redis)
{
    private static RedisKey[] Keys(string account, string ip) =>
    [
        $"attempts:{account}", $"lockout:{account}",
        $"login:ip:attempts:{Hash(ip)}", $"login:ip:lockout:{Hash(ip)}"
    ];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<int> RemainingSecondsAsync(string account, string ip)
    {
        var keys = Keys(account, ip);
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            "return math.max(0, redis.call('TTL', KEYS[1]), redis.call('TTL', KEYS[2]))",
            [keys[1], keys[3]]);
        return (int)result;
    }

    public async Task RecordFailureAsync(string account, string ip)
    {
        // Counter increment, expiry and lock creation must be one atomic operation.
        await redis.GetDatabase().ScriptEvaluateAsync("""
            for i = 1, 3, 2 do
                if redis.call('EXISTS', KEYS[i+1]) == 0 then
                    local n = redis.call('INCR', KEYS[i])
                    if n == 1 then redis.call('EXPIRE', KEYS[i], 900) end
                    if n >= 5 then
                        redis.call('SET', KEYS[i+1], 'locked', 'EX', 900)
                        redis.call('DEL', KEYS[i])
                    end
                end
            end
            return 1
            """, Keys(account, ip));
    }

    public async Task<int> CompleteLoginAsync(string account, string ip)
    {
        // A parallel failed login may have locked the account since the initial check.
        var keys = Keys(account, ip);
        var result = await redis.GetDatabase().ScriptEvaluateAsync("""
            local remaining = math.max(0, redis.call('TTL', KEYS[2]), redis.call('TTL', KEYS[4]))
            if remaining == 0 then redis.call('DEL', KEYS[1]) end
            return remaining
            """, keys);
        return (int)result;
    }
}
