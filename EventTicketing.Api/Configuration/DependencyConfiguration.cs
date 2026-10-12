using System.Net;
using System.Text.RegularExpressions;
using Npgsql;
using StackExchange.Redis;

namespace EventTicketing.Api.Configuration;

public sealed record DependencyConfiguration(string Postgres, ConfigurationOptions Redis)
{
    public static DependencyConfiguration Read(IConfiguration configuration, string environment)
    {
        var testing = environment.Equals("Testing", StringComparison.OrdinalIgnoreCase);
        var local = testing || environment.Equals("Development", StringComparison.OrdinalIgnoreCase);
        var postgres = Expand(configuration.GetConnectionString("DefaultConnection"));
        var redisValue = configuration["Redis:ConnectionString"];
        var redis = Expand(string.IsNullOrWhiteSpace(redisValue) ? configuration["REDIS_CONNECTION"] : redisValue);
        NpgsqlConnectionStringBuilder database;
        ConfigurationOptions cache;
        try
        {
            database = new NpgsqlConnectionStringBuilder(postgres);
            cache = ConfigurationOptions.Parse(redis);
        }
        catch
        {
            // Parser exceptions can include configuration values. Never propagate them.
            throw new InvalidOperationException("Invalid PostgreSQL or Redis configuration; values are withheld.");
        }

        if (string.IsNullOrWhiteSpace(database.Host) || string.IsNullOrWhiteSpace(database.Database)
            || cache.EndPoints.Count == 0)
            throw new InvalidOperationException("Explicit PostgreSQL host/database and Redis endpoint are required.");

        if (local)
        {
            if (!IsLoopback(database.Host) || cache.EndPoints.Any(endpoint => !IsLocalEndpoint(endpoint))
                || !string.IsNullOrEmpty(cache.ServiceName))
                throw new InvalidOperationException("Development/Testing dependencies must use loopback addresses; remote destinations are rejected.");

            if (testing && (!database.Database.StartsWith("event_ticketing_test_", StringComparison.Ordinal)
                || cache.DefaultDatabase != 15))
                throw new InvalidOperationException("Testing requires an isolated event_ticketing_test_* database and Redis database 15.");
        }

        return new DependencyConfiguration(database.ConnectionString, cache);
    }

    private static string Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Dependency configuration is required; no credential defaults are provided.");
        return Regex.Replace(value, @"\$\{([A-Z_]+)\}", match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value)
            ?? throw new InvalidOperationException("A required dependency environment variable is missing."));
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool IsLocalEndpoint(EndPoint endpoint) => endpoint switch
    {
        IPEndPoint ip => IPAddress.IsLoopback(ip.Address),
        DnsEndPoint dns => IsLoopback(dns.Host),
        _ => false
    };
}
