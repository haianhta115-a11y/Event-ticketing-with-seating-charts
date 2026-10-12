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

namespace EventTicketing.Tests.Infrastructure;

internal sealed class LocalApplicationFactory(LocalPostgresDatabase database) : WebApplicationFactory<Program>
{
    public CapturedLogs Logs { get; } = new();
    public FakeEmailDelivery Email { get; } = new();
    public TestClock Clock { get; } = new();
    public bool UseTestClock { get; set; }
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
            ["ConnectionStrings:DefaultConnection"] = database.ConnectionString,
            ["Redis:ConnectionString"] = dependencies.Redis.ToString(),
            ["Jwt:Key"] = signingKey,
            ["Email:Mode"] = "Disabled",
            ["Email:ActivationUrl"] = "http://localhost:5173/activate",
            ["BackgroundJobs:SeatHoldCleanupEnabled"] = "false"
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
            if (UseTestClock) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); }
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
