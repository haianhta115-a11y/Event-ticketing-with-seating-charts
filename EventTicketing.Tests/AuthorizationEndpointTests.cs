using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using StackExchange.Redis;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class AuthorizationEndpointTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private int ownEvent, otherEvent, heldSeat, freeSeat;
    private readonly string suffix = Guid.NewGuid().ToString("N");
    private const string Password = "AuthorizationTest123!";
    private string Email(int id) => $"auth-{id}-{suffix}@example.test";

    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        var db = database.Context;
        var roles = new[] { "Buyer", "Organizer", "GateStaff", "Accountant", "Admin" };
        for (var i = 0; i < roles.Length; i++) db.Roles.Add(new EventTicketing.Api.Entities.Role { Id = i + 1, Name = roles[i] });
        foreach (var (id, role) in new[] { (101, 1), (102, 1), (201, 2), (202, 2), (301, 3), (401, 4), (501, 5), (601, 0), (602, 1) })
        {
            var user = new User
            {
                Id = id,
                Email = Email(id),
                Username = $"user-{id}-{suffix}",
                Name = "Authorization fixture",
                Status = id == 602 ? "Inactive" : "Active",
                PasswordHash = PasswordHasher.HashPassword(Password)
            };
            if (role > 0) user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        }
        await LocalPostgresDatabase.EnsureShowtimeAsync(db, 1);
        await LocalPostgresDatabase.EnsureShowtimeAsync(db, 2);
        var own = await db.Showtimes.Include(s => s.Event).SingleAsync(s => s.Id == 1);
        var other = await db.Showtimes.Include(s => s.Event).SingleAsync(s => s.Id == 2);
        own.Event.OwnerUserId = 201; other.Event.OwnerUserId = 202;
        ownEvent = own.EventId; otherEvent = other.EventId;
        var category = new SeatCategory { ShowtimeId = 1, Name = "Standard", NormalizedName = "STANDARD" };
        var held = new Seat
        {
            ShowtimeId = 1,
            SeatCategory = category,
            Row = "A",
            Number = "1",
            Status = SeatStatus.Held,
            HeldByUserId = 101,
            HeldUntil = DateTime.UtcNow.AddMinutes(10)
        };
        var free = new Seat { ShowtimeId = 1, SeatCategory = category, Row = "A", Number = "2", Status = SeatStatus.Available };
        db.Seats.AddRange(held, free);
        await db.SaveChangesAsync();
        heldSeat = held.Id; freeSeat = free.Id;
        factory = new LocalApplicationFactory(database);
        client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/api/events/admin")]
    [InlineData("/api/showtimes/1/seats/my-hold?currentUserId=101")]
    [InlineData("/api/seating-charts/template")]
    public async Task ProtectedRead_Anonymous_Returns401(string path) =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);

    [Fact]
    public async Task ProtectedMutation_Anonymous_CannotUseClientIdentity()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/showtimes/1/seats/{freeSeat}/hold", new { userId = 101 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/showtimes/1/seats/{heldSeat}/hold?userIdQuery=101")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/events", EventBody())).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(SeatStatus.Available, (await db.Seats.FindAsync(freeSeat))!.Status);
        Assert.Equal(SeatStatus.Held, (await db.Seats.FindAsync(heldSeat))!.Status);
        Assert.Equal(2, await db.Events.CountAsync());
    }

    [Theory]
    [InlineData(101, "Buyer")]
    [InlineData(301, "GateStaff")]
    [InlineData(401, "Accountant")]
    public async Task Management_WrongRole_Returns403_AndLogs(int id, string role)
    {
        factory.Authenticate(client, id, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/events", EventBody())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/events/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/seating-charts/template")).StatusCode);
        Assert.Contains(factory.Logs.Messages, message => message.Contains("lacks required role/policy"));
        using var db = database.CreateContext();
        Assert.Equal(2, await db.Events.CountAsync());
    }

    [Theory]
    [InlineData(201, "Organizer")]
    [InlineData(501, "Admin")]
    public async Task Management_Authorized_CreatesDraftWithClaimOwner(int id, string role)
    {
        factory.Authenticate(client, id, role);
        var response = await client.PostAsJsonAsync("/api/events", EventBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var db = database.CreateContext();
        var created = await db.Events.FindAsync(body.GetProperty("id").GetInt32());
        Assert.Equal(id, created!.OwnerUserId);
        Assert.Equal(EventStatus.Draft, created.Status);
    }

    [Fact]
    public async Task Organizer_SeesOnlyOwnedEvents_CannotReadOtherEvent()
    {
        factory.Authenticate(client, 201, "Organizer");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/events/admin");
        Assert.Equal(ownEvent, Assert.Single(list.EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/events/{ownEvent}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/events/{otherEvent}")).StatusCode);
        Assert.Contains(factory.Logs.Messages, message => message.Contains("Authorization denied"));
    }

    [Fact]
    public async Task Admin_SeesAllEvents_AndReadsOtherOwnersEvent()
    {
        factory.Authenticate(client, 501, "Admin");
        var list = await client.GetFromJsonAsync<JsonElement>("/api/events/admin");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/events/{otherEvent}")).StatusCode);
    }

    [Theory]
    [InlineData(101, "Buyer")]
    [InlineData(201, "Organizer")]
    public async Task Import_Forbidden_DoesNotChangeSeats(int id, string role)
    {
        factory.Authenticate(client, id, role);
        using var form = ImportBody();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/showtimes/2/seats/import", form)).StatusCode);
        using var db = database.CreateContext();
        Assert.False(await db.Seats.AnyAsync(s => s.ShowtimeId == 2));
        Assert.Equal(2, await db.Seats.CountAsync());
    }

    [Theory]
    [InlineData(202, "Organizer")]
    [InlineData(501, "Admin")]
    public async Task Import_OwnerOrAdmin_IsAuthorized(int id, string role)
    {
        factory.Authenticate(client, id, role);
        using var form = ImportBody();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/showtimes/2/seats/import", form)).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(1, await db.Seats.CountAsync(s => s.ShowtimeId == 2));
    }

    [Theory]
    [InlineData("Organizer")]
    [InlineData("GateStaff")]
    [InlineData("Accountant")]
    [InlineData("Admin")]
    public async Task SeatBooking_WrongRole_Returns403(string role)
    {
        factory.Authenticate(client, 201, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/showtimes/1/seats/{freeSeat}/hold", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/showtimes/1/seats/my-hold")).StatusCode);
    }

    [Fact]
    public async Task Buyer_Hold_UsesClaimsInsteadOfBodyIdentity()
    {
        factory.Authenticate(client, 102, "Buyer");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/showtimes/1/seats/{freeSeat}/hold", new { userId = 101 })).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(102, (await db.Seats.FindAsync(freeSeat))!.HeldByUserId);
    }

    [Fact]
    public async Task Buyer_CannotReleaseOrRestoreAnotherUsersHold_WithForgedQuery()
    {
        factory.Authenticate(client, 102, "Buyer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/showtimes/1/seats/{heldSeat}/hold?userIdQuery=101")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/showtimes/1/seats/my-hold?currentUserId=101")).StatusCode);
        using var db = database.CreateContext();
        var seat = await db.Seats.FindAsync(heldSeat);
        Assert.Equal(101, seat!.HeldByUserId);
        Assert.Equal(SeatStatus.Held, seat.Status);
    }

    [Fact]
    public async Task Buyer_CanRestoreAndReleaseOwnHold_DespiteForgedQuery()
    {
        factory.Authenticate(client, 101, "Buyer");
        var response = await client.GetAsync("/api/showtimes/1/seats/my-hold?currentUserId=102");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(101, body.GetProperty("userId").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/showtimes/1/seats/{heldSeat}/hold?userIdQuery=102")).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal(SeatStatus.Available, (await db.Seats.FindAsync(heldSeat))!.Status);
    }

    [Fact]
    public async Task PublicSeatChart_DoesNotTrustQueryIdentity()
    {
        var chart = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/1/seats?currentUserId=101");
        Assert.Equal("HeldByOther", chart.GetProperty("seats")[0].GetProperty("displayStatus").GetString());
        factory.Authenticate(client, 101, "Buyer");
        chart = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/1/seats?currentUserId=102");
        Assert.Equal("HeldByMe", chart.GetProperty("seats")[0].GetProperty("displayStatus").GetString());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("malformed")]
    [InlineData("identity")]
    public async Task InvalidJwt_Returns401(string kind)
    {
        var token = kind switch
        {
            "expired" => factory.Token(201, ["Organizer"], DateTime.UtcNow.AddSeconds(-1)),
            "signature" => factory.Token(201, ["Organizer"], wrongSignature: true),
            "identity" => factory.Token(0, ["Organizer"]),
            _ => "tampered.jwt.token"
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/events/admin")).StatusCode);
    }

    [Fact]
    public async Task UndeclaredRoute_DeniesAuthenticatedUser_AndAnonymous()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/weatherforecast")).StatusCode);
        factory.Authenticate(client, 501, "Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/weatherforecast")).StatusCode);
        // Bare [Authorize] must also deny: it must not bypass the fallback policy.
        var policy = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.DefaultPolicy;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "501"), new Claim(ClaimTypes.Role, "Admin")], "Bearer"));
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(principal, null, policy)).Succeeded);
    }

    [Fact]
    public async Task PublicEndpoints_RemainAnonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/events/open")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/showtimes/1")).StatusCode);
    }

    [Theory]
    [InlineData(101, "Buyer")]
    [InlineData(201, "Organizer")]
    [InlineData(501, "Admin")]
    public async Task Login_Valid_ReturnsRealJwt_WithAssignedRole(int id, string role)
    {
        var login = await LoginAsync(Email(id), Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("userId").GetInt32());
        Assert.Equal(role, Assert.Single(body.GetProperty("roles").EnumerateArray()).GetString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        Assert.Equal(id == 101 ? HttpStatusCode.Forbidden : HttpStatusCode.OK, (await client.GetAsync("/api/events/admin")).StatusCode);
    }

    [Fact]
    public async Task Login_WithoutAssignedRole_DoesNotInventBuyerRole()
    {
        var login = await LoginAsync(Email(601), Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("roles").GetArrayLength());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/showtimes/1/seats/my-hold")).StatusCode);
    }

    [Theory]
    [InlineData("Inactive")]
    [InlineData("Locked")]
    public async Task Login_DisabledAccount_CannotGetToken(string status)
    {
        database.Context.Users.Single(u => u.Id == 602).Status = status;
        await database.Context.SaveChangesAsync();
        var login = await LoginAsync(Email(602), Password);
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.DoesNotContain("token", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_WrongPassword_DoesNotRevealAccountExistence()
    {
        var existing = await LoginAsync(Email(101), "wrong");
        var missing = await LoginAsync(Email(999), "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, existing.StatusCode);
        Assert.Equal(existing.StatusCode, missing.StatusCode);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_FiveFailures_LocksSixth_WithTtl_AcrossHostsAndAliases()
    {
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Email(101), "wrong")).StatusCode);
        await using var secondFactory = new LocalApplicationFactory(database);
        using var second = secondFactory.CreateClient();
        var locked = await second.PostAsJsonAsync("/api/auth/login", new { email = $"user-101-{suffix}", password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(body.GetProperty("remainingSeconds").GetInt32(), 1, 900);
        using var redis = await ConnectionMultiplexer.ConnectAsync(LocalPostgresDatabase.VerifyDependencies().Redis);
        Assert.InRange((await redis.GetDatabase().KeyTimeToLiveAsync($"lockout:{Email(101)}"))!.Value.TotalSeconds, 1, 900);
    }

    [Fact]
    public async Task Login_IpLimit_AppliesAcrossUnknownAccounts_WithoutTrustingForwardedHeader()
    {
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Email(900 + i), "wrong")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.42");
        var locked = await LoginAsync(Email(101), Password);
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(body.GetProperty("remainingSeconds").GetInt32(), 1, 900);
    }

    [Fact]
    public async Task Login_Success_ClearsConsecutiveAccountFailures()
    {
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Email(101), "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Email(101), Password)).StatusCode);
        // Different verified peer: test account reset independently from the separate IP budget.
        await using var secondFactory = new LocalApplicationFactory(database);
        using var second = secondFactory.CreateClient();
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsJsonAsync("/api/auth/login", new { email = Email(101), password = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/api/auth/login", new { email = Email(101), password = Password })).StatusCode);
    }

    [Fact]
    public async Task Login_ExpiredAccountLock_AllowsLoginAgain()
    {
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(Email(101), "wrong")).StatusCode);
        using var redis = await ConnectionMultiplexer.ConnectAsync(LocalPostgresDatabase.VerifyDependencies().Redis);
        // Expire only this test's GUID account key. No flush/reset or shared-key deletion.
        Assert.True(await redis.GetDatabase().KeyExpireAsync($"lockout:{Email(101)}", TimeSpan.Zero));
        await using var secondFactory = new LocalApplicationFactory(database);
        using var second = secondFactory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/api/auth/login", new { email = Email(101), password = Password })).StatusCode);
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) => client.PostAsJsonAsync("/api/auth/login", new { email, password });

    [Fact]
    public async Task Users_EmailUniqueness_IsEnforcedByPostgres()
    {
        using var db = database.CreateContext();
        db.Users.Add(new User { Email = Email(101), Name = "Duplicate", PasswordHash = PasswordHasher.HashPassword(Password) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        using var read = database.CreateContext();
        Assert.Equal(1, await read.Users.CountAsync(user => user.Email == Email(101)));
    }
    private static object EventBody() => new { name = "Authorized event", description = "Fixture", location = "Local", ownerUserId = 999 };
    private static MultipartFormDataContent ImportBody()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("""{"layoutName":"Test","venue":"Local","totalCapacity":1,"seats":[{"section":"Standard","row":"A","seatNumber":"1","price":100,"status":"Available"}]}""", Encoding.UTF8, "application/json"), "file", "chart.json");
        return form;
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        await database.DisposeAsync();
    }
}
