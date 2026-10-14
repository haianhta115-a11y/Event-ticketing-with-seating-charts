using EventTicketing.Api.Configuration;
using EventTicketing.LocalSetup;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace EventTicketing.Tests;

public class TeamLocalSetupTests
{
    private const string Target = "event_ticketing_test_team_0123456789abcdef0123456789abcdef";

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("event_ticketing", "127.0.0.1", 5432)]
    [InlineData("event_ticketing_test_1a1_0d54ff0d02094cd78cc71284befb4ca1", "127.0.0.1", 5432)]
    [InlineData("event_ticketing_db01_restore", "127.0.0.1", 5432)]
    [InlineData("event_ticketing_test_team_bad", "127.0.0.1", 5432)]
    [InlineData(Target, "example.neon.tech", 5432)]
    [InlineData(Target, "localhost", 5432)]
    [InlineData(Target, "127.0.0.1,example.test", 5432)]
    [InlineData(Target, "/tmp", 5432)]
    [InlineData(Target, "127.0.0.1", 6543)]
    public void UnsafeInitializerTargetsRejectedBeforeConnections(string target, string host, int port)
    {
        var value = new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = target, Username = "local" };
        Assert.Throws<InvalidOperationException>(() => FreshLocalDatabase.Validate(value.ConnectionString, target));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InitializerRequiresExactEffectiveDatabaseAndNoHistoricalSeedOperations()
    {
        Assert.Throws<InvalidOperationException>(() => FreshLocalDatabase.Validate("Host=127.0.0.1;Username=local;Database=other", Target));
        using var context = FreshLocalDatabase.Context($"Host=127.0.0.1;Username=local;Database={Target}");
        var operations = FreshLocalDatabase.SchemaOperations(context);
        Assert.Contains(operations, operation => operation is CreateTableOperation { Name: "sales_transitions" });
        Assert.DoesNotContain(operations, operation => operation is InsertDataOperation or UpdateDataOperation or DeleteDataOperation
            or DropTableOperation or DropColumnOperation);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("team_123:", "team_123:attempts:user")]
    [InlineData("", "attempts:user")]
    public void RedisPrefixIsExplicitAndLegacyDefaultUnchanged(string prefix, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Redis:KeyPrefix"] = prefix }).Build();
        Assert.Equal(expected, new RedisKeyScope(configuration).Key("attempts:user"));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("*")]
    [InlineData("shared no delimiter")]
    [InlineData("prefix:*:")]
    public void RedisPrefixRejectsAmbiguousOrUnsafeIdentifiers(string prefix)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Redis:KeyPrefix"] = prefix }).Build();
        Assert.Throws<InvalidOperationException>(() => new RedisKeyScope(configuration));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FreshSchemaAndLegitimateFixture_NoHistoricalUsers_RefusesAnyExistingTarget()
    {
        var dependencies = LocalPostgresDatabase.VerifyDependencies();
        var target = "event_ticketing_test_team_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(dependencies.Postgres) { Database = target }.ConnectionString;
        await FreshLocalDatabase.Initialize(connection, target);
        var schema = await FreshLocalDatabase.Verify(connection, target);
        await using var context = FreshLocalDatabase.Context(connection);
        Assert.Empty(await context.Users.ToArrayAsync());
        Assert.Empty(await context.UserRoles.ToArrayAsync());
        Assert.Empty(await context.SalesTransitions.ToArrayAsync());
        Assert.Equal(5, await context.Roles.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => FreshLocalDatabase.Initialize(connection, target));
        Assert.Equal(schema, await FreshLocalDatabase.Verify(connection, target));
        await FreshLocalDatabase.Provision(connection, target, "organizer@example.test", "Organizer", Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FreshLocalDatabase.Provision(connection, target,
            "organizer@example.test", "Admin", Guid.NewGuid().ToString("N")));
        var account = await context.Users.Include(u => u.UserRoles).ThenInclude(r => r.Role).SingleAsync();
        Assert.Equal("Active", account.Status);
        Assert.Equal("Organizer", Assert.Single(account.UserRoles).Role.Name);
        Assert.Equal(schema, await FreshLocalDatabase.Verify(connection, target));
    }
}
