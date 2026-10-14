using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using EventTicketing.Api.Configuration;
using EventTicketing.Api.Services;
using Xunit.Abstractions;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class BrowseAndReleaseTests(ITestOutputHelper output) : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private int[] ids = [];
    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        database.Context.Users.AddRange(new[] { 101, 102 }.Select(id => new User
        { Id = id, Email = $"release-{id}@example.test", Username = $"release-{id}", Name = "Fixture", PasswordHash = "unused", Status = "Active" }));
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 10);
        var category = new SeatCategory { ShowtimeId = 10, Name = "Standard", NormalizedName = "STANDARD" };
        database.Context.SeatCategories.Add(category); await database.Context.SaveChangesAsync();
        database.Context.Seats.AddRange(Enumerable.Range(1, 3).Select(i => new Seat
        { ShowtimeId = 10, SeatCategoryId = category.Id, Row = "A", Number = i.ToString(), X = i - 1, Y = 0 }));
        await database.Context.SaveChangesAsync();
        ids = await database.Context.Seats.OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();
        factory = new LocalApplicationFactory(database) { UseTestClock = true, HoldDurationMinutes = 3 };
        client = factory.CreateClient(); factory.Authenticate(client, 101, "Buyer");
    }

    [Fact]
    public async Task EmptyMapAndMissingShowtime_AreDistinctAndNeverReturnAnEmptyGridContract()
    {
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 20);
        var populated = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats");
        Assert.True(populated.GetProperty("hasSeatMap").GetBoolean());
        Assert.All(populated.GetProperty("seats").EnumerateArray(), seat =>
            Assert.Equal("Standard", seat.GetProperty("category").GetString()));
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/20/seats");
        Assert.False(empty.GetProperty("hasSeatMap").GetBoolean());
        Assert.Empty(empty.GetProperty("seats").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/showtimes/999999/seats")).StatusCode);
    }

    [Fact]
    public async Task ConfiguredDuration_IsUsedForFirstHold_ButNeverChangesAnExistingDeadline()
    {
        var first = await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        var deadline = body.GetProperty("heldUntilUtc").GetDateTime();
        Assert.Equal(factory.Clock.Now.AddMinutes(3).UtcDateTime, deadline);
        factory.Clock.Now = factory.Clock.Now.AddSeconds(30);
        var second = await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[1]}/hold", new { });
        Assert.Equal(deadline, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetDateTime());
        var recovered = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/my-hold");
        Assert.Equal(deadline, recovered.GetProperty("heldUntilUtc").GetDateTime());
        Assert.Equal(150, recovered.GetProperty("remainingSeconds").GetInt32());
    }

    [Fact]
    public async Task ReleaseOneOfThree_PreservesOthers_AndLastReleaseRemovesSession()
    {
        var hold = await client.PostAsJsonAsync("/api/showtimes/10/seats/hold", new { seatIds = ids });
        var deadline = (await hold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetDateTime();
        factory.Clock.Now = factory.Clock.Now.AddSeconds(10);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/showtimes/10/seats/{ids[0]}/hold")).StatusCode);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/my-hold");
        Assert.Equal(2, session.GetProperty("heldSeats").GetArrayLength());
        Assert.Equal(deadline, session.GetProperty("heldUntilUtc").GetDateTime());
        using var other = factory.CreateClient(); factory.Authenticate(other, 102, "Buyer");
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { })).StatusCode);
        foreach (var id in ids.Skip(1)) Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/showtimes/10/seats/{id}/hold")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/showtimes/10/seats/my-hold")).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(102, (await db.Seats.SingleAsync(s => s.Id == ids[0])).HeldByUserId);
        Assert.All(await db.Seats.Where(s => s.Id != ids[0]).ToListAsync(), s => Assert.Null(s.HeldUntil));
    }

    [Fact]
    public async Task CachedFeed_DoesNotServeAFeedWhoseNearestShowtimeIsNowInThePast()
    {
        await database.Context.Showtimes.Where(s => s.Id == 10).ExecuteUpdateAsync(x => x.SetProperty(s => s.StartTime, factory.Clock.Now.AddMinutes(1).UtcDateTime));
        var first = await client.GetFromJsonAsync<JsonElement>("/api/events/open");
        Assert.Single(first.GetProperty("items").EnumerateArray());
        factory.Clock.Now = factory.Clock.Now.AddMinutes(2);
        var second = await client.GetFromJsonAsync<JsonElement>("/api/events/open");
        Assert.Empty(second.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ConfigurationChange_OnlyAffectsANewSession_NotRemainingHolds()
    {
        var held = await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { });
        var deadline = (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetDateTime();
        factory.Clock.Now = factory.Clock.Now.AddSeconds(10);
        using var db = database.CreateContext();
        var service = new SeatReservationService(db, factory.Clock, Options.Create(new SeatHoldOptions { DurationMinutes = 5 }));
        Assert.Equal(deadline, (await service.HoldSeatAsync(10, ids[1], 101)).HeldUntilUtc);
        foreach (var id in ids.Take(2)) Assert.True((await service.ReleaseSeatAsync(10, id, 101)).IsSuccess);
        var fresh = await service.HoldSeatAsync(10, ids[2], 101);
        Assert.Equal(factory.Clock.Now.AddMinutes(5).UtcDateTime, fresh.HeldUntilUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public async Task InvalidDuration_FailsStartupInsteadOfIssuingUnsafeHolds(int minutes)
    {
        await using var invalid = new LocalApplicationFactory(database) { HoldDurationMinutes = minutes };
        Assert.Throws<OptionsValidationException>(() => invalid.CreateClient());
    }

    private async Task SeedPublicEvents()
    {
        // Synthetic, explicitly owned fixture records. No inferred legacy ownership/mapping.
        database.Context.Events.AddRange(Enumerable.Range(1, 200).Select(i => new Event
        {
            Name = $"Public fixture {i:D3}",
            Description = "Isolated feed fixture",
            Location = "Fixture venue",
            OwnerUserId = 101,
            Status = EventStatus.Published,
            Showtimes = [new Showtime { Id = 1000 + i, Status = ShowtimeStatus.OnSale, StartTime = factory.Clock.Now.AddDays(2).AddMinutes(i / 2).UtcDateTime }]
        }));
        await database.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Feed200Events_PagesWithoutDuplicates_InNearestTimeOrder_AndCachesFor30Seconds()
    {
        await SeedPublicEvents();
        var all = new List<(int id, DateTime start)>(); string? cursor = null;
        do
        {
            var url = "/api/events/open" + (cursor == null ? "" : "?cursor=" + Uri.EscapeDataString(cursor));
            var page = await client.GetFromJsonAsync<JsonElement>(url);
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 20);
            all.AddRange(items.Select(e => (e.GetProperty("eventId").GetInt32(), e.GetProperty("nearestShowtime").GetProperty("startTime").GetDateTime())));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor != null);
        Assert.Equal(201, all.Count); Assert.Equal(201, all.Select(e => e.id).Distinct().Count());
        Assert.Equal(all.OrderBy(e => e.start).ThenBy(e => e.id).ToArray(), all.ToArray());
        using var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(LocalPostgresDatabase.VerifyDependencies().Redis);
        var key = factory.RedisKey($"events:open:v3:{database.Context.Database.GetDbConnection().Database}:revision=0:limit=20:cursor=none");
        var ttl = await redis.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl); Assert.InRange(ttl.Value.TotalSeconds, 1, 30);
        Assert.Equal(15, redis.GetDatabase().Database);
        var malformed = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{long.MaxValue}_1"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/events/open?cursor=" + Uri.EscapeDataString(malformed))).StatusCode);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task LocalLatency_Feed200Showtimes_HoldAndRelease_P95WithinBacklogTargets()
    {
        await SeedPublicEvents();
        client.Dispose(); await factory.DisposeAsync();
        factory = new LocalApplicationFactory(database) { HoldDurationMinutes = 3, LoadTestPoolSize = 8 };
        factory.UseKestrel(0); client = factory.CreateClient(); factory.Authenticate(client, 101, "Buyer");
        var feed = new List<double>(); var holds = new List<double>(); var releases = new List<double>();
        for (var i = 0; i < 33; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/events/open")).StatusCode);
            if (i >= 3) feed.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { })).StatusCode);
            if (i >= 3) holds.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();
            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/showtimes/10/seats/{ids[0]}/hold")).StatusCode);
            if (i >= 3) releases.Add(timer.Elapsed.TotalMilliseconds);
        }
        static double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
        output.WriteLine($"FIX05_LATENCY|samples=30|warmup=3|feedShowtimes=201|feedP95Ms={P95(feed):F2}|holdP95Ms={P95(holds):F2}|releaseP95Ms={P95(releases):F2}|localWarmCache=true");
        Assert.True(P95(feed) < 500); Assert.True(P95(holds) < 300); Assert.True(P95(releases) < 300);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RealBrowser_PublicPagination_Release_EmptyMap_ThreeStates_2000SeatsAndTouch()
    {
        await SeedPublicEvents();
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 20, ShowtimeStatus.Draft);
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 30);
        var unknownCategory = new SeatCategory { ShowtimeId = 30, Name = "Unknown geometry", NormalizedName = "UNKNOWN GEOMETRY" };
        database.Context.SeatCategories.Add(unknownCategory); await database.Context.SaveChangesAsync();
        database.Context.Seats.Add(new Seat { ShowtimeId = 30, SeatCategoryId = unknownCategory.Id, Row = "A", Number = "1" });
        var categoryId = await database.Context.Seats.Where(s => s.Id == ids[0]).Select(s => s.SeatCategoryId).SingleAsync();
        database.Context.Seats.AddRange(Enumerable.Range(0, 1997).Select(i => new Seat
        { ShowtimeId = 10, SeatCategoryId = categoryId, Row = $"R{i / 50:D2}", Number = (i % 50 + 1).ToString(), X = i % 50, Y = i / 50 + 1 }));
        await database.Context.SaveChangesAsync();
        var batchIds = await database.Context.Seats.Where(s => s.ShowtimeId == 10 && s.Id != ids[0] && s.Id != ids[1])
            .OrderBy(s => s.Id).Take(3).Select(s => s.Id).ToArrayAsync();
        await database.Context.Seats.Where(s => s.Id == ids[0]).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SeatStatus.Held)
            .SetProperty(s => s.HeldByUserId, 102).SetProperty(s => s.HeldUntil, DateTime.UtcNow.AddMinutes(10)));
        await database.Context.Seats.Where(s => s.Id == ids[1]).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SeatStatus.Sold));
        client.Dispose(); await factory.DisposeAsync();
        factory = new LocalApplicationFactory(database) { EnableCleanup = true, LoadTestPoolSize = 8 };
        factory.UseKestrel(0); client = factory.CreateClient();
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("FIX03_NODE") ?? "node")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.Combine(repo, "frontend") };
        start.ArgumentList.Add(Path.Combine(repo, "frontend/tests/browser/browseRelease.mjs"));
        start.Environment["VITE_API_PROXY_TARGET"] = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        start.Environment["FIX05_BROWSER_FIXTURE"] = JsonSerializer.Serialize(new
        { showId = 10, emptyShowId = 20, unknownShowId = 30, ids, batchIds, token = factory.Token(101, ["Buyer"]), otherToken = factory.Token(102, ["Buyer"]) });
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, "Browser smoke failed: " + await stderr);
        output.WriteLine(await stdout);
        using var db = database.CreateContext();
        Assert.Equal(2000, await db.Seats.CountAsync(s => s.ShowtimeId == 10));
        Assert.Equal(SeatStatus.Sold, (await db.Seats.SingleAsync(s => s.Id == ids[1])).Status);
        Assert.Equal(0, await db.Seats.CountAsync(s => s.HeldByUserId == 101));
        Assert.Null((await db.Seats.SingleAsync(s => s.ShowtimeId == 30)).X);
    }

    public async Task DisposeAsync()
    {
        client.Dispose(); await factory.DisposeAsync();
        if (factory.LoadTestPoolSize.HasValue)
        {
            using var connection = new Npgsql.NpgsqlConnection(factory.LoadTestConnection);
            Npgsql.NpgsqlConnection.ClearPool(connection);
        }
        await database.DisposeAsync();
    }
}
