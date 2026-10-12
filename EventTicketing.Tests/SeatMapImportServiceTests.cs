using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.SeatMaps;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EventTicketing.Tests;

public sealed class SeatMapImportServiceTests
{
    [Fact]
    public async Task ImportAsync_CreatesAllSeatsAndMissingCategories()
    {
        await using var database = await TestDatabase.CreateAsync();
        var importer = new SeatMapImportService(database.Context, TimeProvider.System);

        var result = await importer.ImportAsync(10, Map(
            ("A", "1", "VIP"),
            ("A", "2", "VIP"),
            ("B", "1", "Standard")));

        Assert.Equal(3, result.SeatCount);
        Assert.Equal(2, result.CreatedCategoryCount);
        Assert.False(result.ReplacedExistingMap);
        Assert.Equal(3, await database.Context.Seats.CountAsync());
        Assert.Equal(2, await database.Context.SeatCategories.CountAsync());
        Assert.All(
            await database.Context.Seats.ToListAsync(),
            seat => Assert.Equal(SeatStatus.Available, seat.Status));
    }

    [Fact]
    public async Task ImportAsync_ReplacesTheWholeExistingMap()
    {
        await using var database = await TestDatabase.CreateAsync();
        var importer = new SeatMapImportService(database.Context, TimeProvider.System);
        await importer.ImportAsync(20, Map(("OLD", "1", "Standard"), ("OLD", "2", "Standard")));

        var result = await importer.ImportAsync(20, Map(("NEW", "7", "VIP")));

        var seat = Assert.Single(await database.Context.Seats.ToListAsync());
        Assert.True(result.ReplacedExistingMap);
        Assert.Equal("NEW", seat.Row);
        Assert.Equal("7", seat.Number);
        Assert.Equal("VIP", (await database.Context.SeatCategories.SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAsync_WhenASeatWasSold_BlocksReplacementAndKeepsOldMap()
    {
        await using var database = await TestDatabase.CreateAsync();
        var importer = new SeatMapImportService(database.Context, TimeProvider.System);
        await importer.ImportAsync(30, Map(("A", "1", "VIP")));
        var oldSeat = await database.Context.Seats.SingleAsync();
        oldSeat.Status = SeatStatus.Sold;
        await database.Context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<SeatMapReplacementBlockedException>(
            () => importer.ImportAsync(30, Map(("B", "1", "Standard"))));

        Assert.Equal(1, exception.SoldSeats);
        Assert.Equal(0, exception.ActiveHolds);
        Assert.Equal("A", (await database.Context.Seats.SingleAsync()).Row);
    }

    [Fact]
    public async Task ImportAsync_WhenASeatHasAnActiveHold_BlocksReplacementAndKeepsOldMap()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var importer = new SeatMapImportService(database.Context, clock);
        await importer.ImportAsync(40, Map(("A", "1", "VIP")));
        var oldSeat = await database.Context.Seats.SingleAsync();
        oldSeat.Status = SeatStatus.Held;
        oldSeat.HeldByUserId = 99;
        oldSeat.HeldUntil = clock.GetUtcNow().AddMinutes(10).UtcDateTime;
        await database.Context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<SeatMapReplacementBlockedException>(
            () => importer.ImportAsync(40, Map(("B", "1", "Standard"))));

        Assert.Equal(0, exception.SoldSeats);
        Assert.Equal(1, exception.ActiveHolds);
        Assert.Equal("A", (await database.Context.Seats.SingleAsync()).Row);
    }

    [Fact]
    public async Task ImportAsync_WhenAHoldExpired_AllowsReplacement()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
        var importer = new SeatMapImportService(database.Context, clock);
        await importer.ImportAsync(50, Map(("A", "1", "VIP")));
        var oldSeat = await database.Context.Seats.SingleAsync();
        oldSeat.Status = SeatStatus.Held;
        oldSeat.HeldByUserId = 99;
        oldSeat.HeldUntil = clock.GetUtcNow().AddMinutes(-1).UtcDateTime;
        await database.Context.SaveChangesAsync();

        await importer.ImportAsync(50, Map(("B", "1", "Standard")));

        var replacement = await database.Context.Seats.SingleAsync();
        Assert.Equal("B", replacement.Row);
        Assert.Equal(SeatStatus.Available, replacement.Status);
    }

    [Fact]
    public async Task ImportAsync_WhenWritingBatchContainingSeat1500Fails_RollsBackEverything()
    {
        var failure = new FailOnSaveInterceptor(4);
        await using var database = await TestDatabase.CreateAsync(failure);
        var importer = new SeatMapImportService(database.Context, TimeProvider.System);
        var document = new SeatMapImportDocument
        {
            Seats = Enumerable.Range(1, 2_000)
                .Select<int, SeatMapImportItem?>(number => new SeatMapImportItem
                {
                    Row = "A",
                    Number = number.ToString(),
                    Category = "Standard"
                })
                .ToList()
        };
        failure.Arm();

        await Assert.ThrowsAsync<SimulatedWriteException>(
            () => importer.ImportAsync(60, document));

        database.Context.ChangeTracker.Clear();
        Assert.Equal(0, await database.Context.Seats.CountAsync());
        Assert.Equal(0, await database.Context.SeatCategories.CountAsync());
    }

    [Fact]
    public async Task ImportAsync_WhenReplacingMapFails_RestoresTheOldMapAndCategories()
    {
        var failure = new FailOnSaveInterceptor(4);
        await using var database = await TestDatabase.CreateAsync(failure);
        var importer = new SeatMapImportService(database.Context, TimeProvider.System);
        await importer.ImportAsync(65, Map(("OLD", "1", "VIP")));
        var replacement = new SeatMapImportDocument
        {
            Seats = Enumerable.Range(1, 2_000)
                .Select<int, SeatMapImportItem?>(number => new SeatMapImportItem
                {
                    Row = "NEW",
                    Number = number.ToString(),
                    Category = "Standard"
                })
                .ToList()
        };
        failure.Arm();

        await Assert.ThrowsAsync<SimulatedWriteException>(
            () => importer.ImportAsync(65, replacement));

        database.Context.ChangeTracker.Clear();
        var oldSeat = Assert.Single(await database.Context.Seats.ToListAsync());
        Assert.Equal("OLD", oldSeat.Row);
        Assert.Equal("VIP", (await database.Context.SeatCategories.SingleAsync()).Name);
    }

    [Fact]
    public async Task ImportAsync_WhenASeatEntryIsNull_ReturnsValidationErrorWithoutWriting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var importer = new SeatMapImportService(database.Context, TimeProvider.System);
        var document = new SeatMapImportDocument
        {
            Seats = [null]
        };

        var exception = await Assert.ThrowsAsync<SeatMapValidationException>(
            () => importer.ImportAsync(70, document));

        Assert.Contains("seats[0] cannot be null.", exception.Errors);
        Assert.Equal(0, await database.Context.Seats.CountAsync());
    }

    private static SeatMapImportDocument Map(params (string Row, string Number, string Category)[] seats)
    {
        return new SeatMapImportDocument
        {
            Seats = seats.Select<(string Row, string Number, string Category), SeatMapImportItem?>(seat => new SeatMapImportItem
            {
                Row = seat.Row,
                Number = seat.Number,
                Category = seat.Category
            }).ToList()
        };
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, AppDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public AppDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync(params IInterceptor[] interceptors)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptors)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FailOnSaveInterceptor(int saveNumber) : SaveChangesInterceptor
    {
        private int count;
        private bool armed;

        public void Arm()
        {
            count = 0;
            armed = true;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (armed && Interlocked.Increment(ref count) == saveNumber)
            {
                throw new SimulatedWriteException();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class SimulatedWriteException : Exception;

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
