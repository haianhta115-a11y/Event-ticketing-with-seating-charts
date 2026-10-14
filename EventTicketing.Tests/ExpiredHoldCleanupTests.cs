using EventTicketing.Api.Entities;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using EventTicketing.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class ExpiredHoldCleanupTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private readonly TestClock clock = new();
    private int expiredId, liveId, soldId, boundaryId;

    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        foreach (var id in new[] { 101, 102 }) database.Context.Users.Add(new User
        { Id = id, Email = $"cleanup-{id}@example.test", Username = $"cleanup-{id}", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 10);
        var category = new SeatCategory { ShowtimeId = 10, Name = "Standard", NormalizedName = "STANDARD" };
        database.Context.SeatCategories.Add(category); await database.Context.SaveChangesAsync();
        clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(clock.Now.ToUnixTimeMilliseconds());
        var expired = new Seat { ShowtimeId = 10, SeatCategoryId = category.Id, Row = "A", Number = "1", Status = SeatStatus.Held, HeldByUserId = 101, HeldUntil = clock.Now.AddMinutes(-1).UtcDateTime };
        var live = new Seat { ShowtimeId = 10, SeatCategoryId = category.Id, Row = "A", Number = "2", Status = SeatStatus.Held, HeldByUserId = 101, HeldUntil = clock.Now.AddMinutes(5).UtcDateTime };
        var sold = new Seat { ShowtimeId = 10, SeatCategoryId = category.Id, Row = "A", Number = "3", Status = SeatStatus.Sold };
        var boundary = new Seat { ShowtimeId = 10, SeatCategoryId = category.Id, Row = "A", Number = "4", Status = SeatStatus.Held, HeldByUserId = 101, HeldUntil = clock.Now.UtcDateTime };
        database.Context.Seats.AddRange(expired, live, sold, boundary); await database.Context.SaveChangesAsync();
        expiredId = expired.Id; liveId = live.Id; soldId = sold.Id; boundaryId = boundary.Id;
    }

    private async Task AssertStartupCleanup()
    {
        var stop = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < stop)
        {
            await using var db = database.CreateContext();
            if (await db.Seats.CountAsync(s => (s.Id == expiredId || s.Id == boundaryId) && s.Status == SeatStatus.Available) == 2) return;
            await Task.Delay(25);
        }
        Assert.Fail("Startup did not drain expired backlog before the first one-minute timer tick.");
    }

    [Fact]
    public async Task ApplicationStartup_DrainsOverdueBacklogWithoutWaitingOneMinute()
    {
        await using var factory = new LocalApplicationFactory(database) { UseTestClock = true, EnableCleanup = true };
        factory.Clock.Now = clock.Now;
        using var client = factory.CreateClient(); await AssertStartupCleanup();
        await using var db = database.CreateContext();
        Assert.Equal(clock.Now.AddMinutes(5).UtcDateTime, (await db.Seats.FindAsync(liveId))!.HeldUntil);
        Assert.Equal(SeatStatus.Sold, (await db.Seats.FindAsync(soldId))!.Status);
    }

    [Fact]
    public async Task ExpirationBoundary_RepeatCleanup_IsIdempotentAndPreservesValidAndSold()
    {
        await using var db = database.CreateContext(); var cleanup = new ExpiredSeatHoldCleanup(db, clock);
        Assert.Equal(2, await cleanup.ExecuteAsync());
        var first = JsonSerializer.Serialize(await db.Seats.AsNoTracking().OrderBy(s => s.Id).ToListAsync());
        Assert.Equal(0, await cleanup.ExecuteAsync()); Assert.Equal(0, await cleanup.ExecuteAsync());
        Assert.Equal(first, JsonSerializer.Serialize(await db.Seats.AsNoTracking().OrderBy(s => s.Id).ToListAsync()));
        await using var fresh = database.CreateContext();
        var expired = (await fresh.Seats.FindAsync(expiredId))!;
        Assert.Equal(SeatStatus.Available, expired.Status); Assert.Null(expired.HeldByUserId); Assert.Null(expired.HeldUntil);
        Assert.Equal(SeatStatus.Available, (await fresh.Seats.FindAsync(boundaryId))!.Status);
        Assert.Equal(SeatStatus.Held, (await fresh.Seats.FindAsync(liveId))!.Status);
        Assert.Equal(clock.Now.AddMinutes(5).UtcDateTime, (await fresh.Seats.FindAsync(liveId))!.HeldUntil);
        Assert.Equal(SeatStatus.Sold, (await fresh.Seats.FindAsync(soldId))!.Status);
    }

    [Fact]
    public async Task ConcurrentCleanupWorkers_TotalChangesEqualExpiredCount_NoValidHoldReleased()
    {
        await using var a = database.CreateContext(); await using var b = database.CreateContext();
        var changes = await Task.WhenAll(new ExpiredSeatHoldCleanup(a, clock).ExecuteAsync(), new ExpiredSeatHoldCleanup(b, clock).ExecuteAsync());
        Assert.Equal(2, changes.Sum());
        await using var verify = database.CreateContext();
        Assert.Equal(2, await verify.Seats.CountAsync(s => s.Status == SeatStatus.Available));
        Assert.Equal(clock.Now.AddMinutes(5).UtcDateTime, (await verify.Seats.FindAsync(liveId))!.HeldUntil);
        Assert.Equal(SeatStatus.Sold, (await verify.Seats.FindAsync(soldId))!.Status);
    }

    [Theory]
    [InlineData(101, false)]
    [InlineData(102, false)]
    [InlineData(102, true)]
    public async Task CleanupWaitingOnConcurrentWriter_RechecksRenewedReplacedOrSoldRow(int newOwner, bool sold)
    {
        await using var writer = database.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM seats WHERE id={expiredId} FOR UPDATE");
        var newDeadline = clock.Now.AddMinutes(sold ? -10 : 10).UtcDateTime;
        // Controlled fixture transaction represents the replacement/renewal committing
        // while cleanup is blocked on its row. No original/protected DB connection.
        await writer.Seats.Where(s => s.Id == expiredId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, sold ? SeatStatus.Sold : SeatStatus.Held)
            .SetProperty(x => x.HeldByUserId, newOwner).SetProperty(x => x.HeldUntil, newDeadline));
        await using var cleaner = database.CreateContext();
        var pending = new ExpiredSeatHoldCleanup(cleaner, clock).ExecuteAsync();
        var limit = DateTime.UtcNow.AddSeconds(3); var waiting = false;
        while (DateTime.UtcNow < limit)
        {
            await using var observe = database.CreateContext();
            await observe.Database.OpenConnectionAsync();
            await using var command = observe.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0) { waiting = true; break; }
            await Task.Delay(20);
        }
        // Release our fixture lock even if the observation assertion fails.
        await transaction.CommitAsync(); var changed = await pending;
        Assert.True(waiting, "Cleanup must actually wait on the writer to exercise PostgreSQL recheck.");
        Assert.Equal(1, changed); // only the independent exactly-expired boundary row
        await using var verify = database.CreateContext(); var seat = (await verify.Seats.FindAsync(expiredId))!;
        Assert.Equal(sold ? SeatStatus.Sold : SeatStatus.Held, seat.Status);
        Assert.Equal(newOwner, seat.HeldByUserId); Assert.Equal(newDeadline, seat.HeldUntil);
    }

    [Fact]
    public async Task RestartEnabledApp_DrainsNewBacklog_LeavesDeadlineAndMyHoldUnchanged()
    {
        var liveDeadline = clock.Now.AddMinutes(5).UtcDateTime;
        await using (var first = new LocalApplicationFactory(database) { UseTestClock = true, EnableCleanup = true })
        {
            first.Clock.Now = clock.Now; using var client = first.CreateClient(); await AssertStartupCleanup();
        }
        // A new overdue fixture hold arrives while the app instance is stopped.
        await database.Context.Seats.Where(s => s.Id == expiredId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, SeatStatus.Held).SetProperty(x => x.HeldByUserId, 102)
            .SetProperty(x => x.HeldUntil, clock.Now.AddSeconds(-1).UtcDateTime));
        await using var restarted = new LocalApplicationFactory(database) { UseTestClock = true, EnableCleanup = true };
        restarted.Clock.Now = clock.Now;
        using var api = restarted.CreateClient(); await AssertStartupCleanup(); restarted.Authenticate(api, 101, "Buyer");
        var session = await api.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/my-hold");
        Assert.Equal(liveDeadline, session.GetProperty("heldUntilUtc").GetDateTime());
        Assert.Equal(300, session.GetProperty("remainingSeconds").GetInt32());
        Assert.Equal(liveId, Assert.Single(session.GetProperty("heldSeats").EnumerateArray()).GetProperty("id").GetInt32());
        for (var i = 0; i < 3; i++)
        {
            using var scope = restarted.Services.CreateScope();
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ExpiredSeatHoldCleanup>().ExecuteAsync());
        }
        await using var verify = database.CreateContext();
        Assert.Equal(SeatStatus.Sold, (await verify.Seats.FindAsync(soldId))!.Status);
    }

    [Fact]
    public async Task BeforeJob_QueryTreatsExpiredAsAvailable_AndBatchReacquiresWithoutChangingLiveDeadline()
    {
        await using var factory = new LocalApplicationFactory(database) { UseTestClock = true };
        factory.Clock.Now = clock.Now; using var client = factory.CreateClient(); factory.Authenticate(client, 101, "Buyer");
        var chart = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats");
        Assert.Equal("Available", chart.GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == expiredId).GetProperty("displayStatus").GetString());
        await using var cleaner = database.CreateContext();
        var cleanup = new ExpiredSeatHoldCleanup(cleaner, clock).ExecuteAsync();
        var hold = client.PostAsJsonAsync("/api/showtimes/10/seats/hold", new { seatIds = new[] { expiredId, boundaryId } });
        await Task.WhenAll(cleanup, hold); // propagate either operation's real failure
        Assert.InRange(await cleanup, 0, 2);
        using var response = await hold;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/my-hold");
        Assert.Equal(3, session.GetProperty("heldSeats").GetArrayLength());
        Assert.Equal(clock.Now.AddMinutes(5).UtcDateTime, session.GetProperty("heldUntilUtc").GetDateTime());
        Assert.All(session.GetProperty("heldSeats").EnumerateArray(), seat => Assert.Equal(clock.Now.AddMinutes(5).UtcDateTime, seat.GetProperty("heldUntilUtc").GetDateTime()));
    }

    [Fact]
    public async Task CancellationBeforePass_ChangesNothing()
    {
        await using var db = database.CreateContext(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExpiredSeatHoldCleanup(db, clock).ExecuteAsync(cancellation.Token));
        await using var verify = database.CreateContext(); Assert.Equal(3, await verify.Seats.CountAsync(s => s.Status == SeatStatus.Held));
    }

    public async Task DisposeAsync() => await database.DisposeAsync();
}
