using System.Net;
using EventTicketing.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace EventTicketing.Tests;

public sealed class LocalRedisStartupTests
{
    [Fact]
    public void StartupOptionsAreBoundedFailClosedAndDoNotMutateConfiguration()
    {
        var configured = ConfigurationOptions.Parse("127.0.0.1:6379,defaultDatabase=15,abortConnect=true");
        var options = LocalRedisStartup.Options(configured);
        Assert.True(options.AbortOnConnectFail);
        Assert.Equal(15, options.DefaultDatabase);
        Assert.Equal(0, options.ConnectRetry);
        Assert.Equal(2000, options.ConnectTimeout);
        Assert.Equal(2000, options.AsyncTimeout);
        Assert.NotEqual(options.ConnectTimeout, configured.ConnectTimeout);
        Assert.Equal(configured.EndPoints.Single(), options.EndPoints.Single());
        configured.DefaultDatabase = 0;
        Assert.Throws<InvalidOperationException>(() => LocalRedisStartup.Options(configured));
        configured.DefaultDatabase = 15; configured.AbortOnConnectFail = false;
        Assert.Throws<InvalidOperationException>(() => LocalRedisStartup.Options(configured));
    }

    [Fact]
    public async Task StartupRetriesOnlyTransientFailuresAndStopsAtThreeAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<RedisConnectionException>(() => LocalRedisStartup.WithStartupGateAsync<int>(() =>
        {
            calls++;
            return Task.FromException<int>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Unit startup fault"));
        }));
        Assert.Equal(3, calls);
        Assert.Equal(7, await LocalRedisStartup.WithStartupGateAsync(() => Task.FromResult(7))); // Gate released after failure.
    }

    [Fact]
    public async Task NonTransientFailuresAreNotRetried()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalRedisStartup.WithStartupGateAsync<int>(() =>
        {
            calls++;
            return Task.FromException<int>(new InvalidOperationException("Unit validation fault"));
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentStartupIsSerializedWithoutSerializingApplicationRequests()
    {
        var active = 0; var maximum = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => LocalRedisStartup.WithStartupGateAsync(async () =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            await Task.Delay(5);
            Interlocked.Decrement(ref active);
            return 1;
        })));
        Assert.Equal(1, maximum);
        Assert.Equal(0, active);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealRedisIsReadyAndFixtureOwnedAcrossApiHostRestart()
    {
        await using var database = await LocalPostgresDatabase.CreateAsync();
        Assert.True(database.Redis.IsConnected);
        Assert.Equal(15, database.Redis.GetDatabase().Database);
        await using (var first = new LocalApplicationFactory(database))
        {
            Assert.Same(database.Redis, first.Services.GetRequiredService<IConnectionMultiplexer>());
            using var client = first.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }
        Assert.True(database.Redis.IsConnected);
        await database.Redis.GetDatabase().PingAsync();
        await using var second = new LocalApplicationFactory(database);
        Assert.Same(database.Redis, second.Services.GetRequiredService<IConnectionMultiplexer>());
        using var restored = second.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await restored.GetAsync("/health/ready")).StatusCode);
        await using var otherDatabase = await LocalPostgresDatabase.CreateAsync();
        await using var other = new LocalApplicationFactory(otherDatabase);
        Assert.NotSame(database.Redis, otherDatabase.Redis);
        Assert.NotEqual(second.RedisKeyPrefix, other.RedisKeyPrefix);
    }
}
