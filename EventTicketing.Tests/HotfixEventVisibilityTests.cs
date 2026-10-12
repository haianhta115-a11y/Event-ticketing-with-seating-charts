using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class HotfixEventVisibilityTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private readonly MissingSeatColumn failure = new();
    private readonly string cacheCursor = Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes($"0_{Guid.NewGuid():N}")));
    private readonly DateTime future = new(2030, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        database.Context.Users.Add(new User { Id = 201, Email = "hotfix@example.test", Username = "hotfix", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        await database.Context.SaveChangesAsync();
        factory = new LocalApplicationFactory(database) { UseTestClock = true, DbInterceptors = [failure] };
        client = factory.CreateClient();
    }
    private async Task<Event> AddEvent(EventStatus state, ShowtimeStatus showState, bool past = false)
    {
        var evt = new Event { Name = "Fixture", Description = "Local", Location = "Local", OwnerUserId = 201, Status = state };
        evt.Showtimes.Add(new Showtime { StartTime = past ? future.AddYears(-20) : future, EndTime = future.AddHours(2), Status = showState });
        database.Context.Events.Add(evt); await database.Context.SaveChangesAsync();
        if (showState == ShowtimeStatus.OnSale)
        {
            var cat = new SeatCategory { ShowtimeId = evt.Showtimes.Single().Id, Name = "VIP", NormalizedName = "VIP" };
            database.Context.SeatCategories.Add(cat); await database.Context.SaveChangesAsync();
            database.Context.Seats.Add(new Seat { ShowtimeId = cat.ShowtimeId, SeatCategoryId = cat.Id, Row = "A", Number = "1" });
            await database.Context.SaveChangesAsync();
        }
        return evt;
    }
    [Theory]
    [InlineData(EventStatus.Draft, ShowtimeStatus.OnSale)]
    [InlineData(EventStatus.Published, ShowtimeStatus.Draft)]
    [InlineData(EventStatus.Published, ShowtimeStatus.Closed)]
    [InlineData(EventStatus.Ended, ShowtimeStatus.OnSale)]
    public async Task NotPubliclySelling_IsExcludedFromAnonymousFeed_AndDetail(EventStatus evtState, ShowtimeStatus showState)
    {
        var evt = await AddEvent(evtState, showState);
        var response = await client.GetAsync($"/api/events/open?cursor={cacheCursor}"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Empty(body.GetProperty("items").EnumerateArray());
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/showtimes/{evt.Showtimes.Single().Id}");
        Assert.False(detail.GetProperty("isOnSale").GetBoolean());
        Assert.False(detail.TryGetProperty("description", out _));
    }
    [Fact]
    public async Task PublishedWithOnSale_IsPublic_NearestUsesOnlyFutureOnSale_WithoutOwnershipLeak()
    {
        var evt = await AddEvent(EventStatus.Published, ShowtimeStatus.OnSale);
        evt.ImageUrl = "https://example.test/poster.png";
        evt.Showtimes.Add(new Showtime { StartTime = future.AddDays(-1), Status = ShowtimeStatus.Draft });
        evt.Showtimes.Add(new Showtime { StartTime = future.AddHours(-1), Status = ShowtimeStatus.Closed });
        await database.Context.SaveChangesAsync();
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/events/open?cursor={cacheCursor}");
        var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(evt.Id, item.GetProperty("eventId").GetInt32());
        Assert.Equal(evt.Showtimes.First().Id, item.GetProperty("nearestShowtime").GetProperty("id").GetInt32());
        Assert.Equal(1, item.GetProperty("showtimeCount").GetInt32());
        Assert.Equal(evt.ImageUrl, item.GetProperty("imageUrl").GetString());
        Assert.False(item.TryGetProperty("ownerUserId", out _));
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/showtimes/{evt.Showtimes.First().Id}");
        Assert.True(detail.GetProperty("isOnSale").GetBoolean());
    }
    [Fact]
    public async Task PastOnSale_IsExcludedFromCurrentListing()
    {
        await AddEvent(EventStatus.Published, ShowtimeStatus.OnSale, past: true);
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/events/open?cursor={cacheCursor}");
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }
    [Fact]
    public async Task SaveSucceeded_ThenMissingSeatColumnMakesListFail_WithoutSecondInsert()
    {
        var evt = await AddEvent(EventStatus.Draft, ShowtimeStatus.Draft);
        database.Context.ChangeTracker.Clear();
        factory.Authenticate(client, 201, "Organizer");
        var response = await client.PostAsJsonAsync($"/api/events/{evt.Id}/showtimes", new { startTime = future.ToString("O"), endTime = future.AddHours(2).ToString("O") });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(created.GetProperty("warnings").EnumerateArray());
        var id = created.GetProperty("id").GetInt32();
        failure.Enabled = true;
        var list = await client.GetAsync($"/api/events/{evt.Id}/showtimes"); Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
        var text = await list.Content.ReadAsStringAsync(); Assert.Contains("Schema database", text); Assert.DoesNotContain("showtime_id", text); Assert.DoesNotContain("42703", text);
        using var db = database.CreateContext();
        Assert.True(await db.Showtimes.AnyAsync(s => s.Id == id && s.EventId == evt.Id));
        Assert.Equal(2, await db.Showtimes.CountAsync(s => s.EventId == evt.Id));
        failure.Enabled = false;
        var reloaded = await client.GetFromJsonAsync<JsonElement>($"/api/events/{evt.Id}/showtimes");
        Assert.Equal(2, reloaded.GetArrayLength()); Assert.Contains(reloaded.EnumerateArray(), s => s.GetProperty("id").GetInt32() == id);
    }
    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); await database.DisposeAsync(); }
    private sealed class MissingSeatColumn : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("seats", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("EXISTS", StringComparison.OrdinalIgnoreCase))
                throw new PostgresException("column seat.showtime_id does not exist", "ERROR", "ERROR", "42703");
            return ValueTask.FromResult(result);
        }
    }
}
