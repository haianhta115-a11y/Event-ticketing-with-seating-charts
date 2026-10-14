using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Api.Services;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class FinalFixTests(ITestOutputHelper output) : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private int[] ids = [];

    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        foreach (var id in new[] { 501, 502, 503, 601, 602 })
            database.Context.Users.Add(new User { Id = id, Email = $"finalfix-{id}@example.test", Username = $"finalfix-{id}", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        await database.Context.SaveChangesAsync();
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 10);
        var evt = await database.Context.Events.SingleAsync(); evt.OwnerUserId = 502;
        await database.Context.SaveChangesAsync();
        await new SeatMapImportService(database.Context, TimeProvider.System).ImportAsync(10, new()
        {
            Seats = Enumerable.Range(1, 3).Select(i => (SeatMapImportItem?)new SeatMapImportItem
            { Row = "A", Number = i.ToString(), Category = "Standard", X = i * 40, Y = 40 }).ToList()
        });
        ids = await database.Context.Seats.OrderBy(s => s.Id).Select(s => s.Id).ToArrayAsync();
        factory = new(database); client = factory.CreateClient();
    }

    [Theory]
    [InlineData(ShowtimeStatus.Draft, EventStatus.Published)]
    [InlineData(ShowtimeStatus.Closed, EventStatus.Published)]
    [InlineData(ShowtimeStatus.OnSale, EventStatus.Draft)]
    public async Task RestrictedCharts_CannotBeReadByAnonymousBuyerOrOtherOrganizer(ShowtimeStatus sales, EventStatus publication)
    {
        await using (var db = database.CreateContext())
        {
            (await db.Showtimes.SingleAsync()).Status = sales;
            (await db.Events.SingleAsync()).Status = publication;
            await db.SaveChangesAsync();
        }
        foreach (var viewer in new[] { (0, ""), (601, "Buyer"), (503, "Organizer"), (601, "Accountant") })
        {
            client.DefaultRequestHeaders.Authorization = null;
            if (viewer.Item1 > 0) factory.Authenticate(client, viewer.Item1, viewer.Item2);
            foreach (var suffix in new[] { "", "/", "?currentUserId=502" })
            {
                var response = await client.GetAsync("/api/showtimes/10/seats" + suffix);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains("SEAT_MAP_NOT_PUBLIC", body);
                Assert.DoesNotContain("heldByUserId", body);
                Assert.DoesNotContain("\"seats\"", body);
            }
        }
        foreach (var viewer in new[] { (502, "Organizer"), (501, "Admin") })
        {
            factory.Authenticate(client, viewer.Item1, viewer.Item2);
            var response = await client.GetAsync("/api/showtimes/10/seats");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, data.GetProperty("seats").GetArrayLength());
            Assert.All(data.GetProperty("seats").EnumerateArray(), s => Assert.False(s.GetProperty("canSelect").GetBoolean()));
        }
    }

    [Fact]
    public async Task ClosedSales_KeepOnlyExistingOwnersRecoveryAndRelease_WithoutNewHold()
    {
        factory.Authenticate(client, 601, "Buyer");
        var held = await client.PostAsJsonAsync("/api/showtimes/10/seats/hold", new { seatIds = ids.Take(2).ToArray() });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        var deadline = (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetString();
        await using (var db = database.CreateContext())
        { (await db.Showtimes.SingleAsync()).Status = ShowtimeStatus.Closed; await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/showtimes/10/seats")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[2]}/hold", new { })).StatusCode);
        using var stranger = factory.CreateClient(); factory.Authenticate(stranger, 602, "Buyer");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/showtimes/10/seats")).StatusCode);
        var release = await client.DeleteAsync($"/api/showtimes/10/seats/{ids[0]}/hold");
        var body = await release.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(deadline, body.GetProperty("session").GetProperty("heldUntilUtc").GetString());
        Assert.Single(body.GetProperty("session").GetProperty("heldSeats").EnumerateArray());
        var last = await client.DeleteAsync($"/api/showtimes/10/seats/{ids[1]}/hold");
        var final = await last.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        Assert.Equal(JsonValueKind.Null, final.GetProperty("session").ValueKind);
        Assert.Empty(final.GetProperty("chart").GetProperty("seats").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/showtimes/10/seats")).StatusCode);
    }

    [Fact]
    public async Task ReleaseResponse_ContainsAuthoritativeChartAndRemainingSession()
    {
        factory.Authenticate(client, 601, "Buyer");
        var held = await client.PostAsJsonAsync("/api/showtimes/10/seats/hold", new { seatIds = ids });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        var deadline = (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetString();
        var response = await client.DeleteAsync($"/api/showtimes/10/seats/{ids[0]}/hold");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(deadline, result.GetProperty("session").GetProperty("heldUntilUtc").GetString());
        Assert.Equal(2, result.GetProperty("session").GetProperty("heldSeats").GetArrayLength());
        Assert.Equal("Available", result.GetProperty("chart").GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == ids[0]).GetProperty("displayStatus").GetString());
    }

    private sealed class SkewedClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddYears(2); }

    [Fact]
    public async Task PostgreSqlTimeControlsExpiryEvenWithSkewedApiClock()
    {
        client.Dispose(); await factory.DisposeAsync();
        factory = new(database) { HostClockOverride = new SkewedClock() }; client = factory.CreateClient();
        await using var db = database.CreateContext();
        var clock = new PostgresReservationClock(); var now = await clock.NowAsync(db);
        var expired = await db.Seats.SingleAsync(s => s.Id == ids[0]);
        expired.Status = SeatStatus.Held; expired.HeldByUserId = 602; expired.HeldUntil = now.AddSeconds(-1);
        var live = await db.Seats.SingleAsync(s => s.Id == ids[1]);
        live.Status = SeatStatus.Held; live.HeldByUserId = 601; live.HeldUntil = now.AddMinutes(6);
        await db.SaveChangesAsync();
        factory.Authenticate(client, 601, "Buyer");
        var chart = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats");
        Assert.Equal("Available", chart.GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == ids[0]).GetProperty("displayStatus").GetString());
        Assert.InRange((chart.GetProperty("serverTimeUtc").GetDateTime() - now).TotalSeconds, 0, 10);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/my-hold");
        Assert.InRange(session.GetProperty("remainingSeconds").GetInt32(), 350, 360);
        Assert.Equal(1, await new ExpiredSeatHoldCleanup(db, clock).ExecuteAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(SeatStatus.Held, (await db.Seats.SingleAsync(s => s.Id == ids[1])).Status);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { })).StatusCode);
        var recovered = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/my-hold");
        Assert.Equal(live.HeldUntil, recovered.GetProperty("heldUntilUtc").GetDateTime());
    }

    [Fact]
    public async Task ConflictLogs_RecordEveryRefusal_WithoutIdentityOrCredentials()
    {
        factory.Authenticate(client, 601, "Buyer");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { })).StatusCode);
        factory.Authenticate(client, 602, "Buyer");
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/showtimes/10/seats/{ids[0]}/hold", new { })).StatusCode);
        var conflicts = factory.Logs.Messages.Where(m => m.StartsWith("Seat hold rejected:")).ToArray();
        Assert.Equal(2, conflicts.Length);
        Assert.All(conflicts, message => { Assert.DoesNotContain("601", message); Assert.DoesNotContain("602", message); Assert.DoesNotContain("@", message); Assert.DoesNotContain("Bearer", message); });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/events", new { name = "No", description = "No", location = "No" })).StatusCode);
        Assert.DoesNotContain(factory.Logs.Messages, m => m.Contains("finalfix-602") || m.Contains("User 602"));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task SeatChart2000_QueryLatencyUnder200Ms()
    {
        var samplePath = Path.GetFullPath("../../../../samples/seating-chart-2000.json", AppContext.BaseDirectory);
        var sample = await File.ReadAllTextAsync(samplePath);
        factory.Authenticate(client, 502, "Organizer");
        foreach (var operation in new[] { "preview", "import" })
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(sample, System.Text.Encoding.UTF8, "application/json"), "file", "seating-chart-2000.json");
            using var response = await client.PostAsync($"/api/showtimes/10/seats/{operation}", form);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (operation == "preview")
            {
                Assert.False(body.GetProperty("persisted").GetBoolean());
                Assert.Equal(2000, body.GetProperty("validation").GetProperty("totalSeats").GetInt32());
                await using var unchanged = database.CreateContext();
                Assert.Equal(3, await unchanged.Seats.CountAsync());
            }
            else Assert.Equal(2000, body.GetProperty("seatCount").GetInt32());
        }
        await using var db = database.CreateContext();
        var service = new SeatReservationService(db, new PostgresReservationClock());
        var samples = new List<double>();
        for (var i = 0; i < 33; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var chart = await service.GetSeatingChartStatusAsync(10);
            Assert.Equal(2000, chart.Count);
            if (i >= 3) samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort(); var p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1];
        Assert.True(p95 < 200, $"Seat-chart query p95 {p95:F2}ms exceeds 200ms.");
        output.WriteLine($"FINALFIX_QUERY|seats=2000|warmup=3|samples=30|p95Ms={p95:F2}|scope=local-service-including-access-and-clock");
    }

    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); await database.DisposeAsync(); }
}
