using EventTicketing.Api.Configuration;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StackExchange.Redis;

namespace EventTicketing.Tests.Infrastructure;

// Every fixture gets a new local database. No migration, reset, drop or application seed.
internal sealed class LocalPostgresDatabase : IAsyncDisposable
{
    private readonly string connectionString;
    public AppDbContext Context { get; }
    public string ConnectionString => connectionString;
    public IConnectionMultiplexer Redis { get; }

    private LocalPostgresDatabase(string connectionString, AppDbContext context, IConnectionMultiplexer redis)
    {
        this.connectionString = connectionString;
        Context = context;
        Redis = redis;
    }

    public static DependencyConfiguration VerifyDependencies()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Environment.GetEnvironmentVariable("BATCH1A1_POSTGRES"),
            ["Redis:ConnectionString"] = Environment.GetEnvironmentVariable("BATCH1A1_REDIS")
        }).Build();
        return DependencyConfiguration.Read(config, "Testing");
    }

    public static async Task<LocalPostgresDatabase> CreateAsync(params IInterceptor[] interceptors)
    {
        var dependencies = VerifyDependencies(); // Before any network operation.
        var redis = await LocalRedisStartup.ConnectAsync(dependencies.Redis);
        try
        {
            var target = new NpgsqlConnectionStringBuilder(dependencies.Postgres)
            {
                Database = "event_ticketing_test_1a1_" + Guid.NewGuid().ToString("N"),
                IncludeErrorDetail = false,
                // Each fixture owns a unique database. Do not retain one idle pool per fixture
                // until process exit; larger suites otherwise exhaust local PostgreSQL clients.
                Pooling = false
            };
            // Preserve the verified host/port/credentials; only the administrative database changes.
            var admin = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var identity = new NpgsqlCommand("SELECT current_database(),inet_server_port()", connection);
                await using (var reader = await identity.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync() || reader.GetString(0) != "postgres" || reader.GetInt32(1) != target.Port)
                        throw new InvalidOperationException("Local fixture server identity mismatch.");
                }
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{target.Database}\"", connection);
                await command.ExecuteNonQueryAsync();
            }

            var context = NewContext(target.ConnectionString, interceptors);
            try
            {
                await context.Database.OpenConnectionAsync();
                await using (var identity = context.Database.GetDbConnection().CreateCommand())
                {
                    identity.CommandText = "SELECT current_database()";
                    if ((string?)await identity.ExecuteScalarAsync() != target.Database || !target.Database.StartsWith("event_ticketing_test_1a1_", StringComparison.Ordinal))
                        throw new InvalidOperationException("New disposable fixture identity mismatch.");
                }
                var model = context.GetService<IDesignTimeModel>().Model;
                var operations = context.GetService<IMigrationsModelDiffer>()
                    .GetDifferences(null, model.GetRelationalModel())
                    .Where(operation => operation is not InsertDataOperation and not UpdateDataOperation and not DeleteDataOperation)
                    .ToList();
                // Schema only, from the real production model. Do not execute HasData demo accounts.
                var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, model);
                foreach (var command in commands)
                    await context.Database.ExecuteSqlRawAsync(command.CommandText);
                // Keep the verified connection open for the complete schema batch. Opening
                // one unpooled Windows socket per DDL command can exhaust ephemeral ports.
                await context.Database.CloseConnectionAsync();
                return new LocalPostgresDatabase(target.ConnectionString, context, redis);
            }
            catch
            {
                await context.DisposeAsync();
                throw;
            }
        }
        catch { redis.Dispose(); throw; }
    }

    private static AppDbContext NewContext(string connection, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);

    public AppDbContext CreateContext(params IInterceptor[] interceptors) => NewContext(connectionString, interceptors);

    public static async Task EnsureShowtimeAsync(AppDbContext db, int showtimeId, ShowtimeStatus status = ShowtimeStatus.OnSale)
    {
        if (await db.Showtimes.AnyAsync(show => show.Id == showtimeId)) return;
        var parent = new Event
        {
            Name = "Integration fixture",
            Description = "Explicit isolated test data",
            Location = "Local test venue",
            Status = EventStatus.Published
        };
        parent.Showtimes.Add(new Showtime { Id = showtimeId, Status = status, StartTime = DateTime.UtcNow.AddDays(1) });
        db.Events.Add(parent);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await Context.DisposeAsync(); }
        finally { Redis.Dispose(); }
        // Leave only the newly created test database for inspection. Never delete shared/local data.
    }
}
