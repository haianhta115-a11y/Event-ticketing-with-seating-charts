using System.Net.Http.Headers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Services;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using EventTicketing.Api.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class RegistrationActivationTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private readonly string suffix = Guid.NewGuid().ToString("N");
    private string Email => $"registration-{suffix}@example.test";
    private object Request(string? email = null, string password = "Password123!", string username = "buyer", string fullName = "Test Buyer") =>
        new { email = email ?? Email, password, username, fullName, status = "Active", roles = new[] { "Admin" } };
    private string Token(int index = 0)
    {
        var body = factory.Email.Messages.ToArray()[index].Body;
        var marker = body.IndexOf("token=", StringComparison.Ordinal) + 6;
        return body.Substring(marker, 64);
    }

    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        database.Context.Roles.Add(new EventTicketing.Api.Entities.Role { Id = 1, Name = "Buyer" });
        await database.Context.SaveChangesAsync();
        factory = new LocalApplicationFactory(database) { UseTestClock = true };
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_CreatesInactiveBuyer_AndHashed24HourToken_WithoutReturningSecrets()
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RegistrationService.GenericMessage, body.GetProperty("message").GetString());
        using var db = database.CreateContext();
        var user = await db.Users.Include(u => u.UserRoles).ThenInclude(r => r.Role).SingleAsync();
        Assert.Equal("Inactive", user.Status);
        Assert.Equal("Buyer", Assert.Single(user.UserRoles).Role.Name);
        var activation = await db.AccountActivations.SingleAsync();
        Assert.Equal(user.Id, activation.UserId);
        Assert.Equal(factory.Clock.Now.UtcDateTime.AddHours(24), activation.ExpiresAtUtc);
        Assert.Null(activation.UsedAtUtc);
        var token = Token();
        Assert.True(token.Length == 64 && token.All(Uri.IsHexDigit));
        Assert.True(activation.TokenHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        var responseLeakedToken = (await response.Content.ReadAsStringAsync()).Contains(token);
        Assert.False(responseLeakedToken);
        Assert.True(factory.Email.Messages.Single().Recipient == Email);
        Assert.All(factory.Logs.Messages, log => { var leakedToken = log.Contains(token); Assert.False(leakedToken); });
    }

    [Theory]
    [InlineData("bad-email", "Password123!", "buyer", "Buyer")]
    [InlineData("a@example.test", "1234567", "buyer", "Buyer")]
    [InlineData("a@example.test", "", "buyer", "Buyer")]
    [InlineData("a@example.test", "Password123!", " ", "Buyer")]
    [InlineData("a@example.test", "Password123!", "buyer", " ")]
    public async Task Register_InvalidInput_DoesNotPersistOrSend(string email, string password, string username, string fullName)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", Request(email, password, username, fullName))).StatusCode);
        using var db = database.CreateContext();
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.UserRoles.ToListAsync());
        Assert.Empty(await db.AccountActivations.ToListAsync());
        Assert.Empty(factory.Email.Messages);
    }

    [Fact]
    public async Task Register_DuplicateAndCaseWhitespace_ReturnSameResponse_WithoutMutatingAccount()
    {
        var first = await client.PostAsJsonAsync("/api/auth/register", Request());
        var second = await client.PostAsJsonAsync("/api/auth/register", Request(" " + Email.ToUpperInvariant() + " ", "ChangedPassword123!", "different", "Changed Name"));
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        using var db = database.CreateContext();
        var user = await db.Users.SingleAsync();
        Assert.Equal("Test Buyer", user.Name);
        Assert.Equal(1, await db.UserRoles.CountAsync());
        Assert.Equal(1, await db.AccountActivations.CountAsync());
        Assert.Single(factory.Email.Messages);
    }

    [Fact]
    public async Task Register_ConcurrentDuplicate_PersistsOnlyOneCompleteAccount()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/auth/register", Request())));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.Single(bodies.Distinct());
        using var db = database.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.UserRoles.CountAsync());
        Assert.Equal(1, await db.AccountActivations.CountAsync());
        Assert.Single(factory.Email.Messages);
    }

    [Fact]
    public async Task Register_MissingBuyerRole_RollsBackWithoutPartialUser()
    {
        database.Context.Roles.Remove(await database.Context.Roles.SingleAsync());
        await database.Context.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/auth/register", Request())).StatusCode);
        using var db = database.CreateContext();
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.AccountActivations.ToListAsync());
        Assert.Empty(factory.Email.Messages);
    }

    [Fact]
    public async Task Register_ActivationInsertFailure_RollsBackUserAndRoleTogether()
    {
        var failure = new ActivationInsertFailure();
        await using var db = database.CreateContext(failure);
        using var redis = await ConnectionMultiplexer.ConnectAsync(LocalPostgresDatabase.VerifyDependencies().Redis);
        var email = new FakeEmailDelivery();
        var service = new RegistrationService(db, email, new EmailDeliveryOptions { ActivationUrl = "http://localhost:5173/activate" },
            redis, factory.Clock, NullLogger<RegistrationService>.Instance);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.RegisterAsync(
            new RegisterRequest(Email, "buyer", "Test Buyer", "Password123!"), CancellationToken.None));
        Assert.True(failure.Triggered);
        await using var read = database.CreateContext();
        Assert.Empty(await read.Users.ToListAsync());
        Assert.Empty(await read.UserRoles.ToListAsync());
        Assert.Empty(await read.AccountActivations.ToListAsync());
        Assert.Empty(email.Messages);
    }

    [Fact]
    public async Task Register_EmailFailure_KeepsInactiveAccount_AndResendCanRecover_WithoutSecretLogs()
    {
        factory.Email.FailDelivery = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/register", Request())).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("Inactive", (await db.Users.SingleAsync()).Status);
        Assert.Equal(1, await db.AccountActivations.CountAsync());
        Assert.Empty(factory.Email.Messages);
        Assert.Contains(factory.Logs.Messages, log => log.Contains("Activation email delivery failed"));
        Assert.All(factory.Logs.Messages, log => Assert.False(log.Contains(Email) || log.Contains("token=")));
        factory.Email.FailDelivery = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email })).StatusCode);
        Assert.Single(factory.Email.Messages);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Token())).StatusCode);
    }

    [Fact]
    public async Task Activation_EnablesCorrectBuyer_AndPreservesAuthorization()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        var blocked = await LoginAsync();
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("ACCOUNT_INACTIVE", await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Token())).StatusCode);
        var login = await LoginAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Buyer", Assert.Single(body.GetProperty("roles").EnumerateArray()).GetString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/events", new { name = "Unauthorized", description = "", location = "Local" })).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("Active", (await db.Users.SingleAsync()).Status);
        Assert.NotNull((await db.AccountActivations.SingleAsync()).UsedAtUtc);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    public async Task Activation_AtOrAfter24Hours_IsRejected(int hours)
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        factory.Clock.Now = factory.Clock.Now.AddHours(hours);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActivateAsync(Token())).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("Inactive", (await db.Users.SingleAsync()).Status);
        Assert.Null((await db.AccountActivations.SingleAsync()).UsedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Token(1))).StatusCode);
    }

    [Fact]
    public async Task Activation_ReplayAndConcurrentUse_OnlyOneSucceeds()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        var token = Token();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ActivateAsync(token)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActivateAsync(token)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Activation_InvalidToken_DoesNotActivate(string token)
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        Assert.Equal(HttpStatusCode.BadRequest, (await ActivateAsync(token)).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("Inactive", (await db.Users.SingleAsync()).Status);
    }

    [Fact]
    public async Task Activation_CannotActivateAnotherAccount()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        await client.PostAsJsonAsync("/api/auth/register", Request($"other-{suffix}@example.test", username: "other"));
        Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Token())).StatusCode);
        using var db = database.CreateContext();
        Assert.Equal("Active", (await db.Users.SingleAsync(u => u.Email == Email)).Status);
        Assert.Equal("Inactive", (await db.Users.SingleAsync(u => u.Email != Email)).Status);
    }

    [Fact]
    public async Task Resend_RotatesToken_AndCannotActivateLockedOrAlreadyActiveUsers()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        var old = Token();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email })).StatusCode);
        Assert.False(old == Token(1));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActivateAsync(old)).StatusCode);
        using var db = database.CreateContext();
        await db.Users.ExecuteUpdateAsync(update => update.SetProperty(u => u.Status, "Locked"));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActivateAsync(Token(1))).StatusCode);
        await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email });
        Assert.Equal(2, factory.Email.Messages.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resend_FivePerHour_IsGenericForExistingAndMissingEmail(bool exists)
    {
        if (exists) await client.PostAsJsonAsync("/api/auth/register", Request());
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = " " + Email.ToUpperInvariant() + " " });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(RegistrationService.GenericMessage, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        }
        var blocked = await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.InRange(int.Parse(blocked.Headers.GetValues("Retry-After").Single()), 1, 3600);
        Assert.Equal(exists ? 6 : 0, factory.Email.Messages.Count);
    }

    [Fact]
    public async Task Resend_ConcurrentRequests_RespectLimit_OnlyLatestTokenRemainsValid()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email })));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(6, factory.Email.Messages.Count);
        var activations = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) activations.Add((await ActivateAsync(Token(i))).StatusCode);
        Assert.Equal(1, activations.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(5, activations.Count(status => status == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Activation_RacingResend_CannotLeaveActiveUserWithUsableToken()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        var token = Token();
        var activating = ActivateAsync(token);
        var resending = client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email });
        var responses = await Task.WhenAll(activating, resending);
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
        using var db = database.CreateContext();
        var record = await db.AccountActivations.SingleAsync();
        var user = await db.Users.SingleAsync();
        if (responses[0].StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal("Active", user.Status);
            Assert.NotNull(record.UsedAtUtc);
            Assert.Single(factory.Email.Messages);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, responses[0].StatusCode);
            Assert.Equal("Inactive", user.Status);
            Assert.Null(record.UsedAtUtc);
            Assert.Equal(2, factory.Email.Messages.Count);
            Assert.Equal(HttpStatusCode.OK, (await ActivateAsync(Token(1))).StatusCode);
        }
    }

    [Fact]
    public async Task Resend_AfterActivation_IsGenericWithoutSendingEmail()
    {
        await client.PostAsJsonAsync("/api/auth/register", Request());
        await ActivateAsync(Token());
        var response = await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Email.Messages);
    }

    [Fact]
    public async Task Resend_RollingHour_RemovesExpiredRequests_AndAllowsNextRequest()
    {
        for (var i = 0; i < 5; i++) await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email })).StatusCode);
        using var redis = await ConnectionMultiplexer.ConnectAsync(LocalPostgresDatabase.VerifyDependencies().Redis);
        var db = redis.GetDatabase();
        var key = (RedisKey)("activation:resend:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Email))));
        var entries = await db.SortedSetRangeByRankAsync(key);
        Assert.Equal(5, entries.Length);
        // Advance only this fixture's request scores; never flush/reset Redis.
        foreach (var entry in entries) await db.SortedSetAddAsync(key, entry, DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/resend-activation", new { email = Email })).StatusCode);
        Assert.Equal(1, await db.SortedSetLengthAsync(key));
    }

    private Task<HttpResponseMessage> ActivateAsync(string token) => client.PostAsJsonAsync("/api/auth/activate", new { token });
    private Task<HttpResponseMessage> LoginAsync() => client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = "Password123!" });
    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        await database.DisposeAsync();
    }
}
