using System.Diagnostics;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Api.Services;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class SeatingChartImportTests(ITestOutputHelper output) : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private readonly ImportWriteFailure failure = new();
    private static object Chart(string row = "A", string number = "1", string category = "VIP", int count = 1) => new
    {
        layoutName = "Fixture",
        seats = Enumerable.Range(0, count).Select(i => new
        {
            section = "Main",
            row,
            seatNumber = count == 1 ? number : (i + 1).ToString(),
            category,
            x = i + 1,
            y = 2,
            status = "Available"
        }).ToArray()
    };
    private Task<HttpResponseMessage> Upload(object chart, HttpClient? actor = null, int showtime = 10) => UploadText(JsonSerializer.Serialize(chart), actor, showtime);
    private async Task<HttpResponseMessage> UploadText(string json, HttpClient? actor = null, int showtime = 10)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "file", "chart.json");
        return await (actor ?? client).PostAsync($"/api/showtimes/{showtime}/seats/import", form);
    }
    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync(failure);
        database.Context.Users.AddRange(new[] { 201, 202, 501 }.Select(id => new User
        {
            Id = id,
            Email = $"chart-{id}@example.test",
            Username = $"chart-{id}",
            Name = "Fixture",
            PasswordHash = "unused",
            Status = "Active"
        }));
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 10);
        var evt = (await database.Context.Showtimes.Include(s => s.Event).SingleAsync(s => s.Id == 10)).Event;
        evt.OwnerUserId = 201;
        await database.Context.SaveChangesAsync();
        factory = new LocalApplicationFactory(database);
        // Fault injection also applies to contexts created by the real endpoint.
        factory.DbInterceptors = [failure];
        client = factory.CreateClient();
        factory.Authenticate(client, 201, "Organizer");
    }

    [Fact]
    public async Task NestedChart_PersistsInheritedRowCategoryAndCoordinates_WithoutRequiredPrice()
    {
        var response = await Upload(new
        {
            layoutName = "Nested",
            sections = new[] { new { name = "Main", category = "VIP",
            rows = new[] { new { rowNumber = "A", seats = new[] { new { seatNumber = "1", x = 4, y = 5 } } } } } }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = database.CreateContext();
        var seat = await db.Seats.Include(s => s.SeatCategory).SingleAsync();
        Assert.Equal("A", seat.Row); Assert.Equal("1", seat.Number); Assert.Equal("VIP", seat.SeatCategory.Name);
        Assert.Equal(4, seat.X); Assert.Equal(5, seat.Y);
        client.DefaultRequestHeaders.Authorization = null;
        var publicMap = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/");
        Assert.True(publicMap.TryGetProperty("serverTimeUtc", out _));
        var item = Assert.Single(publicMap.GetProperty("seats").EnumerateArray());
        Assert.Equal(seat.Id, item.GetProperty("id").GetInt32());
        Assert.Equal(4, item.GetProperty("x").GetInt32());
        Assert.Equal("Available", item.GetProperty("displayStatus").GetString());
    }

    [Fact]
    public async Task Replacement_KeepsMatchingSeatAndCategoryIds_RemovesOnlyUnusedRecords()
    {
        Assert.Equal(HttpStatusCode.OK, (await Upload(Chart(count: 2))).StatusCode);
        using var db = database.CreateContext();
        var before = await db.Seats.OrderBy(s => s.Id).ToListAsync();
        var oldCategory = await db.SeatCategories.SingleAsync();
        Assert.Equal(HttpStatusCode.OK, (await Upload(Chart(number: "1"))).StatusCode);
        db.ChangeTracker.Clear();
        var after = await db.Seats.SingleAsync();
        Assert.Equal(before[0].Id, after.Id); Assert.Equal(oldCategory.Id, after.SeatCategoryId);
        Assert.False(await db.Seats.AnyAsync(s => s.Id == before[1].Id));
        Assert.Equal(1, await db.SeatCategories.CountAsync());
    }

    [Fact]
    public async Task MatchingSeat_CanChangeCategoryWithoutChangingId()
    {
        await Upload(Chart());
        using var db = database.CreateContext();
        var id = (await db.Seats.SingleAsync()).Id;
        Assert.Equal(HttpStatusCode.OK, (await Upload(Chart(category: "Standard"))).StatusCode);
        db.ChangeTracker.Clear();
        var seat = await db.Seats.Include(s => s.SeatCategory).SingleAsync();
        Assert.Equal(id, seat.Id); Assert.Equal("Standard", seat.SeatCategory.Name);
        Assert.Equal(1, await db.SeatCategories.CountAsync());
    }

    [Theory]
    [InlineData("Anonymous", 401)]
    [InlineData("Buyer", 403)]
    [InlineData("GateStaff", 403)]
    [InlineData("Accountant", 403)]
    [InlineData("OtherOrganizer", 403)]
    public async Task UnauthorizedImport_PreservesMap(string role, int expected)
    {
        await Upload(Chart());
        using var db = database.CreateContext(); var id = (await db.Seats.SingleAsync()).Id;
        if (role == "Anonymous") client.DefaultRequestHeaders.Authorization = null;
        else factory.Authenticate(client, 202, role == "OtherOrganizer" ? "Organizer" : role);
        Assert.Equal(expected, (int)(await Upload(Chart(row: "NEW"))).StatusCode);
        db.ChangeTracker.Clear(); Assert.Equal(id, (await db.Seats.SingleAsync()).Id);
        Assert.Equal("A", (await db.Seats.SingleAsync()).Row);
    }

    [Fact]
    public async Task Admin_CanImportAnotherOrganizersChart()
    {
        factory.Authenticate(client, 501, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await Upload(Chart())).StatusCode);
    }

    [Fact]
    public async Task LegacyUpload_ExplicitlyReportsValidationOnly_NoPersistence()
    {
        var json = """{"layoutName":"Legacy","seats":[{"section":"Main","row":"A","seatNumber":"1","price":100}]}""";
        var response = await client.PostAsync("/api/seating-charts/upload", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("persisted").GetBoolean());
        using var db = database.CreateContext(); Assert.Empty(await db.Seats.ToListAsync());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[null]}")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[]}")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\",\"category\":\"VIP\",\"status\":\"Booked\"}]}")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\",\"category\":\"VIP\",\"x\":1.5}]}")]
    public async Task InvalidInput_PreservesOldMap(string json)
    {
        await Upload(Chart());
        using var db = database.CreateContext(); var before = await db.Seats.SingleAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadText(json)).StatusCode);
        db.ChangeTracker.Clear(); var after = await db.Seats.SingleAsync();
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.SeatCategoryId, after.SeatCategoryId);
    }

    [Fact]
    public async Task DuplicateAcrossSections_IsRejectedByDatabaseSeatIdentityBeforeWriting()
    {
        await Upload(Chart());
        var response = await Upload(new
        {
            layoutName = "Duplicate",
            seats = new[] {
            new { section = "Left", row = "A", seatNumber = "1", category = "VIP" },
            new { section = "Right", row = " a ", seatNumber = "1", category = "VIP" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("A", (await db.Seats.SingleAsync()).Row);
    }

    [Theory]
    [InlineData(SeatStatus.Sold)]
    [InlineData(SeatStatus.Held)]
    public async Task SoldOrActiveHold_BlocksReplacementAndPreservesIdentityAndHold(SeatStatus status)
    {
        await Upload(Chart());
        using var db = database.CreateContext(); var seat = await db.Seats.SingleAsync();
        seat.Status = status; if (status == SeatStatus.Held) { seat.HeldByUserId = 123; seat.HeldUntil = DateTime.UtcNow.AddHours(1); }
        await db.SaveChangesAsync();
        var stored = await db.Seats.AsNoTracking().SingleAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(Chart(row: "NEW"))).StatusCode);
        db.ChangeTracker.Clear(); var after = await db.Seats.SingleAsync();
        Assert.Equal(seat.Id, after.Id); Assert.Equal(status, after.Status); Assert.Equal(stored.HeldUntil, after.HeldUntil);
        Assert.Equal(stored.HeldByUserId, after.HeldByUserId);
    }

    [Fact]
    public async Task UnmodelledCascadeReference_IsBlockedRatherThanDeletingHistory()
    {
        await Upload(Chart());
        using var db = database.CreateContext();
        var id = (await db.Seats.SingleAsync()).Id;
        // Test-only history table; no Order/Ticket production feature is introduced.
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE test_seat_history (seat_id integer REFERENCES seats(id) ON DELETE CASCADE)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO test_seat_history VALUES ({id})");
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(Chart(row: "NEW"))).StatusCode);
        Assert.Equal(id, (await db.Seats.SingleAsync()).Id);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM test_seat_history").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkFailureAtSeat1500_RollsBackEntireEndpointImport(bool replace)
    {
        if (replace) await Upload(Chart(row: "OLD"));
        using var db = database.CreateContext();
        var beforeSeats = await db.Seats.AsNoTracking().ToListAsync(); var beforeCategories = await db.SeatCategories.AsNoTracking().ToListAsync();
        failure.Arm();
        var response = await Upload(Chart(row: "NEW", category: "Standard", count: 2000));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("private persistence failure", await response.Content.ReadAsStringAsync());
        Assert.Equal(beforeSeats.Select(s => s.Id), (await db.Seats.AsNoTracking().ToListAsync()).Select(s => s.Id));
        Assert.Equal(beforeCategories.Select(c => c.Id), (await db.SeatCategories.AsNoTracking().ToListAsync()).Select(c => c.Id));
        Assert.DoesNotContain(factory.Logs.Messages, s => s.Contains("private persistence failure"));
    }

    [Fact]
    public async Task ConcurrentImports_EndWithOneCompleteMap_NotMixedRows()
    {
        using var other = factory.CreateClient(); factory.Authenticate(other, 201, "Organizer");
        var responses = await Task.WhenAll(Upload(Chart(row: "FIRST", count: 40)), Upload(Chart(row: "SECOND", count: 50), other));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        using var db = database.CreateContext(); var rows = await db.Seats.ToListAsync();
        var row = Assert.Single(rows.Select(s => s.Row).Distinct());
        Assert.Equal(row == "FIRST" ? 40 : 50, rows.Count);
        Assert.Equal(1, await db.SeatCategories.CountAsync());
    }

    [Fact]
    public async Task HoldCommitsWhileImportWaits_ImportRechecksAndPreservesHold()
    {
        await Upload(Chart());
        using var db = database.CreateContext(); var id = (await db.Seats.SingleAsync()).Id;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var deadline = DateTime.UtcNow.AddMinutes(10);
        await db.Seats.Where(s => s.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(s => s.Status, SeatStatus.Held).SetProperty(s => s.HeldByUserId, 123).SetProperty(s => s.HeldUntil, deadline));
        failure.ResetSignals();
        var pending = Upload(Chart(row: "NEW"));
        await failure.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await pending).StatusCode);
        db.ChangeTracker.Clear(); var seat = await db.Seats.SingleAsync();
        Assert.Equal(id, seat.Id); Assert.Equal(123, seat.HeldByUserId); Assert.Equal(SeatStatus.Held, seat.Status);
    }

    [Fact]
    public async Task ImportCommitsBeforeStaleHoldWrite_DeletedSeatCannotBecomeReserved()
    {
        await Upload(Chart());
        using var db = database.CreateContext(failure); var id = (await db.Seats.SingleAsync()).Id;
        failure.ResetSignals(); failure.PauseAfterLock = true;
        var importing = Upload(Chart(row: "NEW"));
        await failure.LockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var holding = new SeatReservationService(db, TimeProvider.System).HoldSeatAsync(10, id, 123);
            await failure.HoldWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(holding.IsCompleted);
            failure.Resume.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, (await importing).StatusCode);
            Assert.False((await holding).IsSuccess);
            using var verify = database.CreateContext();
            Assert.False(await verify.Seats.AnyAsync(s => s.Id == id));
            Assert.Equal("NEW", (await verify.Seats.SingleAsync()).Row);
            Assert.Equal(SeatStatus.Available, (await verify.Seats.SingleAsync()).Status);
        }
        finally { failure.Resume.TrySetResult(); }
    }

    [Fact]
    public async Task SeatIdentityAndCategoryShowtimeRelationships_AreEnforcedByDatabase()
    {
        await Upload(Chart());
        using var db = database.CreateContext(); var seat = await db.Seats.AsNoTracking().SingleAsync();
        db.Seats.Add(new Seat { ShowtimeId = 10, SeatCategoryId = seat.SeatCategoryId, Row = seat.Row, Number = seat.Number });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Seats.Add(new Seat { ShowtimeId = 999, SeatCategoryId = seat.SeatCategoryId, Row = "B", Number = "1" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(db.Model.FindEntityType(typeof(Seat))!.GetIndexes(), i => i.Properties[0].Name == nameof(Seat.ShowtimeId));
    }

    [Fact]
    public async Task TwoThousandSeats_UseFourMultiRowStatements_AndCompleteUnderFiveSeconds()
    {
        var watch = Stopwatch.StartNew();
        var response = await Upload(Chart(count: 2000)); watch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = database.CreateContext(); Assert.Equal(2000, await db.Seats.CountAsync());
        output.WriteLine($"2000-seat HTTP import: {watch.Elapsed.TotalMilliseconds:F0} ms; bulk commands {failure.BulkCommands}");
        Assert.Equal(4, failure.BulkCommands);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Import took {watch.Elapsed}");
    }

    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); await database.DisposeAsync(); }

    private sealed class ImportWriteFailure : DbCommandInterceptor
    {
        private int count; private bool armed;
        public int BulkCommands => count;
        public void Arm() { count = 0; armed = true; }
        public bool PauseAfterLock { get; set; }
        public TaskCompletionSource LockStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LockAcquired { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HoldWriteStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ResetSignals()
        {
            LockStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); LockAcquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            HoldWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static bool SeatLock(DbCommand c) => c.CommandText.Contains("FROM seats") && c.CommandText.Contains("FOR UPDATE");
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (SeatLock(command)) LockStarted.TrySetResult();
            if (command.CommandText.Contains("UPDATE seats")) HoldWriteStarted.TrySetResult();
            return base.ReaderExecutingAsync(command, data, result, ct);
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken ct = default)
        {
            if (SeatLock(command))
            {
                LockAcquired.TrySetResult();
                if (PauseAfterLock) await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            // ExecuteUpdate uses ExecuteNonQuery; tracked SaveChanges uses ExecuteReader.
            // Observe either path without changing the import/hold ordering assertions.
            if (command.CommandText.Contains("UPDATE seats")) HoldWriteStarted.TrySetResult();
            if (command.CommandText.StartsWith("INSERT INTO seats ", StringComparison.Ordinal))
                if (Interlocked.Increment(ref count) == 3 && armed) throw new InvalidOperationException("private persistence failure");
            return base.NonQueryExecutingAsync(command, data, result, ct);
        }
    }
}
