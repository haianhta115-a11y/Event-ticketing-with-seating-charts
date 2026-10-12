using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class EventManagementTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private int ownId, otherId;
    private static object EventBody(string name = "Concert", string description = "Description", string location = "Venue") =>
        new { name, description, location, ownerUserId = 202, status = "Published" };
    private static object Slot(string start = "2030-10-10T19:00:00+07:00", string? end = "2030-10-10T21:00:00+07:00") =>
        new { startTime = start, endTime = end, eventId = 999, status = "OnSale" };

    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        foreach (var id in new[] { 101, 201, 202, 501 })
            database.Context.Users.Add(new User
            {
                Id = id,
                Email = $"event-{id}@example.test",
                Username = $"event-{id}",
                Name = "Fixture",
                PasswordHash = "unused fixture hash",
                Status = "Active"
            });
        var own = new Event { Name = "Own", Description = "Fixture", Location = "Local", OwnerUserId = 201 };
        var other = new Event { Name = "Other", Description = "Fixture", Location = "Local", OwnerUserId = 202 };
        database.Context.Events.AddRange(own, other);
        await database.Context.SaveChangesAsync();
        ownId = own.Id; otherId = other.Id;
        factory = new LocalApplicationFactory(database) { UseTestClock = true };
        client = factory.CreateClient();
        factory.Authenticate(client, 201, "Organizer");
    }

    [Fact]
    public async Task CreateDraft_UsesClaims_TrimsFields_AndIsNotPublic()
    {
        var response = await client.PostAsJsonAsync("/api/events", EventBody(" Concert "));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var db = database.CreateContext();
        var evt = (await db.Events.FindAsync(body.GetProperty("id").GetInt32()))!;
        Assert.Equal(201, evt.OwnerUserId);
        Assert.Equal(EventStatus.Draft, evt.Status);
        Assert.Equal("Concert", evt.Name);
        Assert.Equal(DateTimeKind.Utc, evt.CreatedAt.Kind);
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime, evt.CreatedAt);
        client.DefaultRequestHeaders.Authorization = null;
        // ticks=0 means first page; unique cursor isolates this fixture's Redis cache.
        var cursor = Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"0_{Guid.NewGuid():N}")));
        var publicEvents = await client.GetFromJsonAsync<JsonElement>($"/api/events/open?cursor={cursor}");
        Assert.Empty(publicEvents.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task UpdateAndList_PreserveOwnerAndStatus()
    {
        var response = await client.PutAsJsonAsync($"/api/events/{ownId}", EventBody("Updated"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var db = database.CreateContext();
        var evt = (await db.Events.FindAsync(ownId))!;
        Assert.Equal("Updated", evt.Name);
        Assert.Equal(201, evt.OwnerUserId);
        Assert.Equal(EventStatus.Draft, evt.Status);
        var list = await client.GetFromJsonAsync<JsonElement>("/api/events/admin");
        Assert.Equal(ownId, Assert.Single(list.EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("Anonymous", 401)]
    [InlineData("Buyer", 403)]
    [InlineData("GateStaff", 403)]
    [InlineData("Accountant", 403)]
    public async Task ProtectedManagement_RejectsWrongIdentity(string role, int status)
    {
        if (role == "Anonymous") client.DefaultRequestHeaders.Authorization = null;
        else factory.Authenticate(client, 101, role);
        Assert.Equal(status, (int)(await client.PostAsJsonAsync("/api/events", EventBody())).StatusCode);
        Assert.Equal(status, (int)(await client.PutAsJsonAsync($"/api/events/{ownId}", EventBody())).StatusCode);
        Assert.Equal(status, (int)(await client.GetAsync($"/api/events/{ownId}/showtimes")).StatusCode);
        Assert.Equal(status, (int)(await client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot())).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(2, await db.Events.CountAsync());
        Assert.Empty(await db.Showtimes.ToListAsync());
        Assert.Equal("Own", (await db.Events.FindAsync(ownId))!.Name);
    }

    [Fact]
    public async Task OtherOrganizer_CannotReadEditOrManageShowtimes()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/events/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/events/{otherId}", EventBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/events/{otherId}/showtimes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/events/{otherId}/showtimes", Slot())).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("Other", (await db.Events.FindAsync(otherId))!.Name);
        Assert.Empty(await db.Showtimes.ToListAsync());
    }

    [Fact]
    public async Task Admin_CanEditAndManageOtherOwnersEvent_WithoutTakingOwnership()
    {
        factory.Authenticate(client, 501, "Admin");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/events/{otherId}", EventBody())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/events/{otherId}/showtimes", Slot())).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/events/{otherId}/showtimes");
        Assert.Single(list.EnumerateArray());
        using var db = database.CreateContext();
        Assert.Equal(202, (await db.Events.FindAsync(otherId))!.OwnerUserId);
    }

    [Theory]
    [InlineData("", "Description", "Venue", "Name")]
    [InlineData("   ", "Description", "Venue", "Name")]
    [InlineData("Name", "", "Venue", "Description")]
    [InlineData("Name", "Description", "   ", "Location")]
    public async Task InvalidEvent_CreateAndUpdateReturnFieldErrors_NoWrites(string name, string description, string location, string field)
    {
        foreach (var response in new[] { await client.PostAsJsonAsync("/api/events", EventBody(name, description, location)),
            await client.PutAsJsonAsync($"/api/events/{ownId}", EventBody(name, description, location)) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        }
        using var db = database.CreateContext();
        Assert.Equal(2, await db.Events.CountAsync());
        Assert.Equal("Own", (await db.Events.FindAsync(ownId))!.Name);
    }

    [Theory]
    [InlineData(201, 5)]
    [InlineData(5, 301)]
    public async Task OversizedFields_AreRejectedBeforeDatabase(int nameLength, int locationLength)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/events",
            EventBody(new string('x', nameLength), location: new string('x', locationLength)))).StatusCode);
    }

    [Fact]
    public async Task Showtime_StoresUtc_ListsRealData_AndPreservesPublicAccess()
    {
        var response = await client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("warnings").EnumerateArray());
        using var db = database.CreateContext();
        var slot = await db.Showtimes.SingleAsync();
        Assert.Equal(ownId, slot.EventId);
        Assert.Equal(ShowtimeStatus.Draft, slot.Status);
        Assert.Equal(new DateTime(2030, 10, 10, 12, 0, 0, DateTimeKind.Utc), slot.StartTime);
        Assert.Equal(new DateTime(2030, 10, 10, 14, 0, 0, DateTimeKind.Utc), slot.EndTime);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/events/{ownId}/showtimes");
        Assert.Equal(slot.Id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetInt32());
        client.DefaultRequestHeaders.Authorization = null;
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/showtimes/{slot.Id}");
        Assert.False(detail.GetProperty("isOnSale").GetBoolean());
    }

    [Theory]
    [InlineData("2020-01-01T00:00:00Z", null, "StartTime")]
    [InlineData("2030-10-10T19:00:00", null, "StartTime")]
    [InlineData("not-a-date", null, "StartTime")]
    [InlineData("2030-10-10T19:00:00+07:00", "2030-10-10T19:00:00+07:00", "EndTime")]
    [InlineData("2030-10-10T19:00:00+07:00", "2030-10-10T18:00:00+07:00", "EndTime")]
    [InlineData("2030-10-10T19:00:00+07:00", "2030-10-10T21:00:00", "EndTime")]
    public async Task InvalidShowtime_ReturnsSpecificFieldError_NoWrite(string start, string? end, string field)
    {
        var response = await client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot(start, end));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        using var db = database.CreateContext();
        Assert.Empty(await db.Showtimes.ToListAsync());
    }

    [Theory]
    [InlineData("2030-10-10T19:00:00+07:00", "2030-10-10T21:00:00+07:00", true)]
    [InlineData("2030-10-10T20:00:00+07:00", "2030-10-10T22:00:00+07:00", true)]
    [InlineData("2030-10-10T21:00:00+07:00", "2030-10-10T22:00:00+07:00", false)]
    [InlineData("2030-10-10T19:00:00+07:00", null, true)]
    public async Task Overlap_WarnsButPersists_AdjacentDoesNotWarn(string start, string? end, bool warning)
    {
        await client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot());
        var response = await client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot(start, end));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(warning ? 1 : 0, body.GetProperty("warnings").GetArrayLength());
        using var db = database.CreateContext();
        Assert.Equal(2, await db.Showtimes.CountAsync());
    }

    [Fact]
    public async Task ConcurrentDuplicateShowtimes_BothPersist_OneGetsWarning()
    {
        using var second = factory.CreateClient();
        factory.Authenticate(second, 201, "Organizer");
        var results = await Task.WhenAll(client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot()),
            second.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot()));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(1, bodies.Sum(b => b.GetProperty("warnings").GetArrayLength()));
        using var db = database.CreateContext();
        Assert.Equal(2, await db.Showtimes.CountAsync());
    }

    [Fact]
    public async Task MissingEvent_Is404_ForUpdateAndShowtimeOperations()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/events/9999", EventBody())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/events/9999/showtimes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/events/9999/showtimes", Slot())).StatusCode);
    }

    [Fact]
    public async Task OwnerForeignKey_RejectsOrphan_AndUserDeletionIsRestricted()
    {
        using var db = database.CreateContext();
        db.Events.Add(new Event { Name = "Invalid", Description = "Fixture", Location = "Local", OwnerUserId = 999 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Users.Remove((await db.Users.FindAsync(201))!);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task EventDeletion_CascadesToItsShowtimes_InIsolatedFixture()
    {
        await client.PostAsJsonAsync($"/api/events/{ownId}/showtimes", Slot());
        using var db = database.CreateContext();
        Assert.Equal(1, await db.Showtimes.CountAsync(s => s.EventId == ownId));
        await db.Events.Where(e => e.Id == ownId).ExecuteDeleteAsync();
        Assert.Empty(await db.Showtimes.Where(s => s.EventId == ownId).ToListAsync());
        Assert.NotNull(await db.Events.FindAsync(otherId));
    }

    public async Task DisposeAsync()
    {
        client.Dispose(); await factory.DisposeAsync(); await database.DisposeAsync();
    }
}
