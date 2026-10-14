using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit.Abstractions;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class BatchHoldApiTests(ITestOutputHelper output) : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private int showId;
    private int[] ids = [];
    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        foreach (var id in new[] { 101, 102 }) database.Context.Users.Add(new User
        { Id = id, Email = $"batch-{id}@example.test", Username = $"batch-{id}", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 10); showId = 10;
        var category = new SeatCategory { ShowtimeId = showId, Name = "Standard", NormalizedName = "STANDARD" };
        database.Context.SeatCategories.Add(category); await database.Context.SaveChangesAsync();
        database.Context.Seats.AddRange(Enumerable.Range(1, 5).Select(i => new Seat
        { ShowtimeId = showId, SeatCategoryId = category.Id, Row = "A", Number = i.ToString(), X = i - 1, Y = 0 }));
        await database.Context.SaveChangesAsync();
        ids = await database.Context.Seats.OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();
        factory = new LocalApplicationFactory(database) { UseTestClock = true };
        client = factory.CreateClient(); factory.Authenticate(client, 101, "Buyer");
    }
    private Task<HttpResponseMessage> Batch(int[]? seats) => client.PostAsJsonAsync($"/api/showtimes/{showId}/seats/hold", new { seatIds = seats, userId = 102 });

    [Fact]
    public async Task BatchRoute_HoldsFiveSeats_WithClaimsOwnerAndOneDeadline()
    {
        var result = await Batch(ids);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        var expiry = body.GetProperty("heldUntilUtc").GetDateTime();
        Assert.Equal(5, body.GetProperty("heldSeats").GetArrayLength());
        using var db = database.CreateContext();
        Assert.All(await db.Seats.ToListAsync(), s => { Assert.Equal(101, s.HeldByUserId); Assert.Equal(expiry, s.HeldUntil); });
    }

    [Theory]
    [InlineData("held")]
    [InlineData("sold")]
    public async Task FiveSeats_ThirdUnavailable_RollsBackAll_ReportsOnlyRejectedIds(string status)
    {
        await database.Context.Seats.Where(s => s.Id == ids[2]).ExecuteUpdateAsync(x => x
            .SetProperty(s => s.Status, status == "sold" ? SeatStatus.Sold : SeatStatus.Held)
            .SetProperty(s => s.HeldByUserId, 102).SetProperty(s => s.HeldUntil, factory.Clock.Now.AddMinutes(3).UtcDateTime));
        var response = await Batch(ids);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([ids[2]], body.GetProperty("rejectedSeatIds").EnumerateArray().Select(i => i.GetInt32()).ToArray());
        Assert.False(body.TryGetProperty("currentSeat", out _));
        Assert.DoesNotContain("heldByUserId", body.GetRawText());
        using var db = database.CreateContext();
        Assert.Equal(0, await db.Seats.CountAsync(s => s.HeldByUserId == 101));
        Assert.Equal(4, await db.Seats.CountAsync(s => s.Status == SeatStatus.Available));
    }

    [Fact]
    public async Task MultipleUnavailableAndMissingIds_AreAllReportedWithoutPartialHolds()
    {
        await database.Context.Seats.Where(s => s.Id == ids[1] || s.Id == ids[3])
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SeatStatus.Sold));
        var selected = ids.Append(999999).ToArray();
        var response = await Batch(selected);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { ids[1], ids[3], 999999 }, body.GetProperty("rejectedSeatIds").EnumerateArray().Select(i => i.GetInt32()).ToArray());
        using var db = database.CreateContext(); Assert.Equal(0, await db.Seats.CountAsync(s => s.Status == SeatStatus.Held));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("oversized")]
    public async Task InvalidBatch_Is400_WithoutWrites(string input)
    {
        var selected = input switch
        {
            "null" => null,
            "empty" => Array.Empty<int>(),
            "duplicate" => new[] { ids[0], ids[0] },
            "negative" => new[] { -1 },
            _ => Enumerable.Range(1, 101).ToArray()
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await Batch(selected)).StatusCode);
        using var db = database.CreateContext(); Assert.Equal(0, await db.Seats.CountAsync(s => s.Status == SeatStatus.Held));
    }

    [Theory]
    [InlineData("Anonymous", 401)]
    [InlineData("Organizer", 403)]
    [InlineData("Accountant", 403)]
    public async Task UnauthorizedBatch_DoesNotWrite(string role, int status)
    {
        if (role == "Anonymous") client.DefaultRequestHeaders.Authorization = null;
        else factory.Authenticate(client, 101, role);
        Assert.Equal(status, (int)(await Batch(ids)).StatusCode);
        using var db = database.CreateContext(); Assert.Equal(0, await db.Seats.CountAsync(s => s.Status == SeatStatus.Held));
    }

    [Fact]
    public async Task BatchReusesExistingDeadline_AndExpiredOtherHoldCanBeAcquired()
    {
        var first = await client.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{ids[0]}/hold", new { });
        var expiry = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetDateTime();
        factory.Clock.Now = factory.Clock.Now.AddSeconds(7);
        await database.Context.Seats.Where(s => s.Id == ids[2]).ExecuteUpdateAsync(x => x.SetProperty(s => s.Status, SeatStatus.Held)
            .SetProperty(s => s.HeldByUserId, 102).SetProperty(s => s.HeldUntil, factory.Clock.Now.AddSeconds(-1).UtcDateTime));
        Assert.Equal(HttpStatusCode.OK, (await Batch(ids)).StatusCode);
        using var db = database.CreateContext();
        Assert.All(await db.Seats.ToListAsync(), s => { Assert.Equal(101, s.HeldByUserId); Assert.Equal(expiry, s.HeldUntil); });
    }

    [Fact]
    public async Task SubMicrosecondClock_ReturnedDeadlineExactlyMatchesPersistedRecovery()
    {
        factory.Clock.Now = factory.Clock.Now.AddTicks(7);
        var response = await Batch(ids);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deadline = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetString();
        var recovered = await client.GetFromJsonAsync<JsonElement>($"/api/showtimes/{showId}/seats/my-hold");
        Assert.Equal(deadline, recovered.GetProperty("heldUntilUtc").GetString());
        using var db = database.CreateContext();
        Assert.All(await db.Seats.ToListAsync(), s => Assert.Equal(DateTime.Parse(deadline!).ToUniversalTime(), s.HeldUntil));
    }

    [Fact]
    public async Task Burst200Requests100Seats_Exactly100Successes100Conflicts_OneOwnerPerSeat()
    {
        // All 200 HTTP requests enter together. A 32-connection pool is the realistic
        // server backpressure, not a client-side concurrency throttle; protects local PG.
        foreach (var user in Enumerable.Range(1000, 200)) database.Context.Users.Add(new User
        { Id = user, Email = $"burst-{user}@example.test", Username = $"burst-{user}", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        var category = await database.Context.SeatCategories.SingleAsync();
        database.Context.Seats.AddRange(Enumerable.Range(6, 95).Select(i => new Seat
        { ShowtimeId = showId, SeatCategoryId = category.Id, Row = "A", Number = i.ToString() }));
        await database.Context.SaveChangesAsync();
        var hundred = await database.Context.Seats.OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();
        await factory.DisposeAsync(); client.Dispose();
        factory = new LocalApplicationFactory(database) { UseTestClock = true, LoadTestPoolSize = 32 };
        client = factory.CreateClient();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/showtimes/{showId}/seats/hold");
            request.Headers.Authorization = new("Bearer", factory.Token(1000 + i, ["Buyer"]));
            request.Content = JsonContent.Create(new { seatIds = new[] { hundred[i % 100] } });
            await start.Task;
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (i, response.StatusCode, body);
        }).ToArray();
        start.SetResult(); var results = await Task.WhenAll(tasks);
        var successes = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflicts = results.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(100, successes); Assert.Equal(100, conflicts);
        using var db = database.CreateContext(); var rows = await db.Seats.OrderBy(s => s.Id).ToArrayAsync();
        Assert.Equal(100, rows.Length);
        foreach (var group in results.GroupBy(r => hundred[r.i % 100]))
        {
            var winner = Assert.Single(group, r => r.StatusCode == HttpStatusCode.OK);
            var loser = Assert.Single(group, r => r.StatusCode == HttpStatusCode.Conflict);
            var row = rows.Single(s => s.Id == group.Key);
            Assert.Equal(1000 + winner.i, row.HeldByUserId); Assert.Equal(SeatStatus.Held, row.Status);
            Assert.Equal(factory.Clock.Now.AddMinutes(10).UtcDateTime, row.HeldUntil);
            Assert.Equal("ghế vừa có người chọn", loser.body.GetProperty("message").GetString());
            Assert.Equal(group.Key, Assert.Single(loser.body.GetProperty("rejectedSeatIds").EnumerateArray()).GetInt32());
        }
        output.WriteLine("FIX03_CONCURRENCY|requests=200|seats=100|success=100|conflicts=100|otherErrors=0|invariants=100/100|pool=32");
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task RealBrowser_SelectFive_ConflictRollback_RetryFour_ReloadPreservesDeadline()
    {
        await factory.DisposeAsync(); client.Dispose();
        factory = new LocalApplicationFactory(database) { EnableCleanup = true }; // real clock, isolated cleanup enabled
        factory.UseKestrel(0); client = factory.CreateClient();
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("FIX03_NODE") ?? "node")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.Combine(repo, "frontend")
        };
        start.ArgumentList.Add(Path.Combine(repo, "frontend/tests/browser/batchHold.mjs"));
        start.Environment["VITE_API_PROXY_TARGET"] = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        start.Environment["FIX03_BROWSER_FIXTURE"] = JsonSerializer.Serialize(new
        { showId, ids, token = factory.Token(101, ["Buyer"]), otherToken = factory.Token(102, ["Buyer"]) });
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, "Browser smoke failed: " + await stderr);
        output.WriteLine(await stdout);
        using var db = database.CreateContext();
        Assert.Equal(4, await db.Seats.CountAsync(s => s.HeldByUserId == 101));
        Assert.Equal(1, await db.Seats.CountAsync(s => s.HeldByUserId == 102));
    }

    public async Task DisposeAsync()
    {
        client.Dispose(); await factory.DisposeAsync();
        if (factory.LoadTestPoolSize.HasValue)
        {
            using var connection = new NpgsqlConnection(factory.LoadTestConnection);
            NpgsqlConnection.ClearPool(connection); // Only this unique fixture pool, no DB deletion.
        }
        await database.DisposeAsync();
    }
}
