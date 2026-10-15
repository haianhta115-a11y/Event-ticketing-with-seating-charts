using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class SalesWorkflowTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private int eventId, showId, seatId;

    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        foreach (var id in new[] { 101, 201, 202, 501 })
            database.Context.Users.Add(new User { Id = id, Email = $"sales-{id}@example.test", Username = $"sales-{id}", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        var evt = new Event { Name = "S07 fixture", Description = "Local", Location = "Local", OwnerUserId = 201 };
        evt.Showtimes.Add(new Showtime { StartTime = new DateTime(2030, 10, 10, 12, 0, 0, DateTimeKind.Utc) });
        database.Context.Events.Add(evt);
        await database.Context.SaveChangesAsync();
        eventId = evt.Id; showId = evt.Showtimes.Single().Id;
        factory = new LocalApplicationFactory(database) { UseTestClock = true };
        client = factory.CreateClient(); factory.Authenticate(client, 201, "Organizer");
    }

    private async Task AddMap()
    {
        var category = new SeatCategory { ShowtimeId = showId, Name = "VIP", NormalizedName = "VIP", Price = 100000m };
        database.Context.SeatCategories.Add(category); await database.Context.SaveChangesAsync();
        var seat = new Seat { ShowtimeId = showId, SeatCategoryId = category.Id, Row = "A", Number = "1" };
        database.Context.Seats.Add(seat); await database.Context.SaveChangesAsync(); seatId = seat.Id;
    }

    [Fact]
    public async Task PublishAndOpen_FromExistingFrontendContracts_ReturnsSuccess()
    {
        await AddMap();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/api/events/{eventId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/showtimes/{showId}/status", new { newStatus = "OnSale" })).StatusCode);
    }

    private Task<HttpResponseMessage> Sales(string? status) => client.PutAsJsonAsync($"/api/showtimes/{showId}/status", new { newStatus = status });
    private async Task Open()
    {
        await AddMap();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/api/events/{eventId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Sales("OnSale")).StatusCode);
    }

    [Theory]
    [InlineData("Draft", "OnSale", 200)]
    [InlineData("Draft", "Closed", 409)]
    [InlineData("OnSale", "Closed", 200)]
    [InlineData("Closed", "OnSale", 200)]
    [InlineData("OnSale", "OnSale", 200)]
    [InlineData("Closed", "Closed", 200)]
    [InlineData("Draft", "Draft", 400)]
    [InlineData("OnSale", "Draft", 400)]
    [InlineData("Closed", "Draft", 400)]
    [InlineData("Draft", "1", 400)]
    [InlineData("Draft", null, 400)]
    public async Task TransitionMatrix(string from, string? to, int expected)
    {
        await AddMap();
        await database.Context.Events.ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Published));
        await database.Context.Showtimes.ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, Enum.Parse<ShowtimeStatus>(from)));
        Assert.Equal(expected, (int)(await Sales(to)).StatusCode);
        using var db = database.CreateContext();
        var actual = (await db.Showtimes.SingleAsync()).Status.ToString();
        Assert.Equal(expected == 200 ? to : from, actual);
        Assert.Equal(expected == 200 && from != to ? 1 : 0, await db.SalesTransitions.CountAsync());
    }

    [Fact]
    public async Task OpeningWithoutMapOrPublication_IsRejectedWithoutMutation()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await Sales("OnSale")).StatusCode);
        await client.PutAsync($"/api/events/{eventId}/publish", null);
        var response = await Sales("OnSale");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SEAT_MAP_REQUIRED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        using var db = database.CreateContext();
        Assert.Equal(ShowtimeStatus.Draft, (await db.Showtimes.SingleAsync()).Status);
        Assert.Single(await db.SalesTransitions.ToListAsync()); // publication only
    }

    [Fact]
    public async Task OpeningWithoutConfiguredPrices_IsRejectedWithUnpricedCategoryNames()
    {
        var category1 = new SeatCategory { ShowtimeId = showId, Name = "VIP", NormalizedName = "VIP", Price = null };
        var category2 = new SeatCategory { ShowtimeId = showId, Name = "Standard", NormalizedName = "STANDARD", Price = null };
        database.Context.SeatCategories.AddRange(category1, category2);
        await database.Context.SaveChangesAsync();
        var seat1 = new Seat { ShowtimeId = showId, SeatCategoryId = category1.Id, Row = "A", Number = "1" };
        var seat2 = new Seat { ShowtimeId = showId, SeatCategoryId = category2.Id, Row = "B", Number = "1" };
        database.Context.Seats.AddRange(seat1, seat2);
        await database.Context.SaveChangesAsync();

        await client.PutAsync($"/api/events/{eventId}/publish", null);
        var response = await Sales("OnSale");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SEAT_PRICING_REQUIRED", body.GetProperty("errorCode").GetString());
        var message = body.GetProperty("message").GetString();
        Assert.Contains("VIP", message);
        Assert.Contains("Standard", message);
        var unpriced = body.GetProperty("unpricedCategories").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("VIP", unpriced);
        Assert.Contains("Standard", unpriced);
    }

    [Theory]
    [InlineData(202, "Organizer", 403)]
    [InlineData(501, "Buyer", 403)]
    [InlineData(501, "Accountant", 403)]
    [InlineData(0, "Anonymous", 401)]
    [InlineData(101, "Admin", 200)]
    public async Task RoleAndOwnershipApplyToEveryTransition(int actor, string role, int expected)
    {
        await AddMap();
        await database.Context.Events.ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Published));
        if (actor == 0) client.DefaultRequestHeaders.Authorization = null;
        else factory.Authenticate(client, actor, role);
        Assert.Equal(expected, (int)(await Sales("OnSale")).StatusCode);
        Assert.Equal(expected, (int)(await client.PutAsync($"/api/events/{eventId}/unpublish", null)).StatusCode);
        Assert.Equal(expected, (int)(await client.PutAsync($"/api/events/{eventId}/publish", null)).StatusCode);
        using var db = database.CreateContext();
        if (expected != 200) Assert.Empty(await db.SalesTransitions.ToListAsync());
    }

    [Fact]
    public async Task CloseReopenAndUnpublish_InvalidateWarmFeed_AndPreserveHolds()
    {
        await Open();
        factory.Authenticate(client, 501, "Buyer");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{seatId}/hold", new { })).StatusCode);
        var holdUrl = $"/api/showtimes/{showId}/seats/my-hold";
        var original = await client.GetFromJsonAsync<JsonElement>(holdUrl);
        async Task<bool> Listed()
        {
            var feed = await client.GetFromJsonAsync<JsonElement>("/api/events/open");
            return feed.GetProperty("items").EnumerateArray().Any(e => e.GetProperty("eventId").GetInt32() == eventId);
        }
        Assert.True(await Listed()); // warm the exact same cache key
        factory.Authenticate(client, 201, "Organizer");
        Assert.Equal(HttpStatusCode.OK, (await Sales("Closed")).StatusCode);
        Assert.False(await Listed());
        Assert.False((await client.GetFromJsonAsync<JsonElement>($"/api/showtimes/{showId}")).GetProperty("isOnSale").GetBoolean());
        factory.Authenticate(client, 501, "Buyer");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{seatId}/hold", new { })).StatusCode);
        var retained = await client.GetFromJsonAsync<JsonElement>(holdUrl);
        Assert.Equal(original.GetRawText(), retained.GetRawText());
        factory.Authenticate(client, 201, "Organizer");
        Assert.Equal(HttpStatusCode.OK, (await Sales("OnSale")).StatusCode);
        Assert.True(await Listed());
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync($"/api/events/{eventId}/unpublish", null)).StatusCode);
        Assert.False(await Listed());
        factory.Authenticate(client, 501, "Buyer");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{seatId}/hold", new { })).StatusCode);
        Assert.Equal(original.GetRawText(), (await client.GetFromJsonAsync<JsonElement>(holdUrl)).GetRawText());
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/showtimes/{showId}/seats/{seatId}/hold")).StatusCode);
        using var db = database.CreateContext();
        var audit = await db.SalesTransitions.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(5, audit.Count);
        Assert.All(audit, a => { Assert.Equal(201, a.ActorUserId); Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime, a.OccurredAtUtc); });
    }

    [Fact]
    public async Task ConcurrentCloseAndHold_LeavesNoPostCloseNewHold()
    {
        await Open();
        using var buyer = factory.CreateClient(); factory.Authenticate(buyer, 501, "Buyer");
        var results = await Task.WhenAll(Sales("Closed"), buyer.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{seatId}/hold", new { }));
        Assert.Equal(HttpStatusCode.OK, results[0].StatusCode);
        Assert.Contains(results[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        Assert.Equal(HttpStatusCode.Conflict, (await buyer.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{seatId}/hold", new { })).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(ShowtimeStatus.Closed, (await db.Showtimes.SingleAsync()).Status);
    }

    [Fact]
    public async Task MissingAuditSchema_RollsBackStatusChange()
    {
        await AddMap();
        // Only this new isolated fixture. Demonstrates code-only rollout fails closed.
        await database.Context.Database.ExecuteSqlRawAsync("DROP TABLE sales_transitions");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PutAsync($"/api/events/{eventId}/publish", null)).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(EventStatus.Draft, (await db.Events.SingleAsync()).Status);
    }

    [Fact]
    public async Task ServerSession_SurvivesNewClient_AndExpiresWithoutWaitingForCleanup()
    {
        await Open();
        factory.Authenticate(client, 501, "Buyer");
        var held = await client.PostAsJsonAsync($"/api/showtimes/{showId}/seats/{seatId}/hold", new { });
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        var deadline = (await held.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heldUntilUtc").GetString();
        factory.Clock.Now = factory.Clock.Now.AddMinutes(4);
        using var reopened = factory.CreateClient(); factory.Authenticate(reopened, 501, "Buyer");
        var session = await reopened.GetFromJsonAsync<JsonElement>($"/api/showtimes/{showId}/seats/my-hold");
        Assert.Equal(deadline, session.GetProperty("heldUntilUtc").GetString());
        Assert.Equal(360, session.GetProperty("remainingSeconds").GetInt32());
        Assert.Single(session.GetProperty("heldSeats").EnumerateArray());
        factory.Clock.Now = factory.Clock.Now.AddMinutes(7);
        Assert.Equal(HttpStatusCode.NotFound, (await reopened.GetAsync($"/api/showtimes/{showId}/seats/my-hold")).StatusCode);
        var seats = await reopened.GetFromJsonAsync<JsonElement>($"/api/showtimes/{showId}/seats");
        Assert.Equal("Available", Assert.Single(seats.GetProperty("seats").EnumerateArray()).GetProperty("displayStatus").GetString());
        using var db = database.CreateContext();
        Assert.Equal(SeatStatus.Held, (await db.Seats.SingleAsync()).Status); // job not run; query still counts it as available
    }

    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); await database.DisposeAsync(); }
}
