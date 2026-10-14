using StackExchange.Redis;

namespace EventTicketing.Tests.Infrastructure;

// Test infrastructure only. No HTTP/business-operation retries, fake cache or
// production Redis option changes. Serialize connection startup, not requests.
internal static class LocalRedisStartup
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal const int MaximumAttempts = 3;

    internal static ConfigurationOptions Options(ConfigurationOptions configured)
    {
        if (configured.DefaultDatabase != 15 || !configured.AbortOnConnectFail)
            throw new InvalidOperationException("Isolated Redis startup requires DB15 and abortConnect=true.");
        var options = configured.Clone();
        options.ConnectRetry = 0; // The bounded outer startup loop owns retries.
        options.ConnectTimeout = 2000;
        options.AsyncTimeout = 2000;
        options.IncludeDetailInExceptions = false;
        return options;
    }

    internal static async Task<T> WithStartupGateAsync<T>(Func<Task<T>> attempt)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Isolated Redis startup queue timed out.");
        try
        {
            for (var number = 1; ; number++)
            {
                try { return await attempt(); }
                catch (Exception error) when (number < MaximumAttempts && error is RedisConnectionException or RedisTimeoutException)
                {
                    await Task.Delay(number * 100);
                }
            }
        }
        finally { Gate.Release(); }
    }

    internal static Task<IConnectionMultiplexer> ConnectAsync(ConfigurationOptions configured) =>
        WithStartupGateAsync<IConnectionMultiplexer>(async () =>
        {
            var connection = await ConnectionMultiplexer.ConnectAsync(Options(configured));
            try
            {
                await connection.GetDatabase(15).PingAsync();
                if (!connection.IsConnected) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Isolated Redis not ready.");
                return connection;
            }
            catch { connection.Dispose(); throw; }
        });
}
