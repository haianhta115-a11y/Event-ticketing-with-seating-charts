using System.Text.RegularExpressions;

namespace EventTicketing.Api.Configuration;

// Empty prefix preserves existing deployments/tests. Each independent local app can
// explicitly isolate all its keys without flushing Redis or changing hold authority.
public sealed class RedisKeyScope
{
    private readonly string prefix;
    public RedisKeyScope(IConfiguration configuration)
    {
        prefix = configuration["Redis:KeyPrefix"] ?? "";
        if (prefix.Length > 100 || (prefix.Length > 0 &&
            !Regex.IsMatch(prefix, @"\A[A-Za-z0-9_-]+:\z", RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Redis key prefix must be an identifier ending with a colon.");
    }
    public string Key(string value) => prefix + value;
}
