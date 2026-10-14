using Microsoft.Extensions.Logging;
using EventTicketing.Api.Email;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using EventTicketing.Api.Data;
using Npgsql;

namespace EventTicketing.Tests.Infrastructure;

internal sealed class LocalApplicationFactory(LocalPostgresDatabase database) : WebApplicationFactory<Program>
{
    public CapturedLogs Logs { get; } = new();
    public string RedisKeyPrefix => "test_" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database + ":";
    public string RedisKey(string key) => RedisKeyPrefix + key;
    public FakeEmailDelivery Email { get; } = new();
    public TestClock Clock { get; } = new();
    public bool UseTestClock { get; set; }
    public TimeProvider? HostClockOverride { get; set; }
    public bool EnableCleanup { get; set; }
    public int? HoldDurationMinutes { get; set; }
    public int? LoadTestPoolSize { get; set; }
    public string LoadTestConnection => new NpgsqlConnectionStringBuilder(database.ConnectionString)
    { Pooling = true, MaxPoolSize = LoadTestPoolSize ?? 32 }.ConnectionString;
    public IInterceptor[] DbInterceptors { get; set; } = [];
    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private readonly IPAddress peerAddress = IPAddress.Parse($"127.{Random.Shared.Next(1, 255)}.{Random.Shared.Next(1, 255)}.{Random.Shared.Next(1, 255)}");

    public string Token(int userId, string[] roles, DateTime? expires = null, bool wrongSignature = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var key = wrongSignature ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) : signingKey;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims,
            expires: expires ?? DateTime.UtcNow.AddHours(2),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
    }

    public void Authenticate(HttpClient client, int userId, params string[] roles) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(userId, roles));

    private Dictionary<string, string?> Settings()
    {
        var dependencies = LocalPostgresDatabase.VerifyDependencies();
        return new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = LoadTestPoolSize.HasValue ? LoadTestConnection : database.ConnectionString,
            ["Redis:ConnectionString"] = dependencies.Redis.ToString(),
            ["Redis:KeyPrefix"] = RedisKeyPrefix,
            ["Jwt:Key"] = signingKey,
            ["Email:Mode"] = "Disabled",
            ["SeatHolds:DurationMinutes"] = (HoldDurationMinutes ?? 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Email:ActivationUrl"] = "http://localhost:5173/activate",
            ["BackgroundJobs:SeatHoldCleanupEnabled"] = EnableCleanup ? "true" : "false"
        };
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration reaches minimal Program before its immediate safety checks.
        // ConfigureAppConfiguration alone is too late for code preceding builder.Build().
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(Settings()));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new PeerAddressFilter(peerAddress)));
        builder.ConfigureServices(services =>
        {
            if (DbInterceptors.Length > 0) services.AddDbContext<AppDbContext>(options => options.AddInterceptors(DbInterceptors));
            services.RemoveAll<IEmailDelivery>();
            services.AddSingleton<IEmailDelivery>(Email);
            if (HostClockOverride != null)
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton(HostClockOverride);
            }
            if (UseTestClock)
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<EventTicketing.Api.Services.IReservationClock>();
                services.AddSingleton<EventTicketing.Api.Services.IReservationClock>(Clock);
            }
        });
    }
}

// Assign the test connection a peer IP. Production never trusts a client IP header.
internal sealed class PeerAddressFilter(IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            await nextMiddleware(context);
        });
        next(app);
    };
}
