using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using EventTicketing.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Npgsql;

var passed = 0;
void Check(string name, string postgres, string redis, string environment, bool allowed)
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:DefaultConnection"] = postgres,
        ["Redis:ConnectionString"] = redis
    }).Build();
    try
    {
        DependencyConfiguration.Read(config, environment);
        if (!allowed) throw new Exception("Unsafe configuration was accepted: " + name);
    }
    catch (InvalidOperationException error)
    {
        if (allowed || error.Message.Contains("private-value")) throw;
    }
    Console.WriteLine("PASS " + name);
    passed++;
}

const string local = "Host=127.0.0.1;Database=event_ticketing_test_checks";
Check("development loopback", local, "127.0.0.1:6379", "Development", true);
Check("testing isolation", local, "localhost:6379,defaultDatabase=15", "Testing", true);
Check("Neon rejected", "Host=example.neon.tech;Database=test;Password=private-value", "localhost:6379", "Development", false);
Check("case-insensitive environment rejected", "Host=example.neon.tech;Database=test", "localhost:6379", "development", false);
Check("unknown remote rejected", "Host=10.0.0.5;Database=test", "localhost:6379", "Testing", false);
Check("multi-host rejected", "Host=localhost,example.neon.tech;Database=test", "localhost:6379", "Development", false);
Check("socket path rejected", "Host=/tmp;Database=test", "localhost:6379", "Development", false);
Check("remote Redis rejected", local, "cache.example.com:6379", "Development", false);
Check("mixed Redis rejected", local, "localhost:6379,cache.example.com:6379", "Development", false);
Check("sentinel discovery rejected", local, "localhost:26379,serviceName=master", "Development", false);
Check("shared test database rejected", "Host=localhost;Database=event_ticketing", "localhost:6379,defaultDatabase=15", "Testing", false);
Check("shared Redis test partition rejected", local, "localhost:6379", "Testing", false);
Check("invalid configuration redacted", "private-value", "localhost:6379", "Development", false);
Check("missing placeholder rejected", "Host=${BATCH1A_UNSET_HOST};Database=test", "localhost:6379", "Development", false);
Check("deployment configuration remains supported (no connection)", "Host=deployment.example.com;Database=deployment", "redis:6379", "Staging", true);
// A later provider must not bypass validation of the final effective value.
var overridden = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:DefaultConnection"] = local,
    ["Redis:ConnectionString"] = "localhost:6379"
}).AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:DefaultConnection"] = "Host=override.neon.tech;Database=test"
}).Build();
try { DependencyConfiguration.Read(overridden, "Development"); throw new Exception("Override bypassed guard"); }
catch (InvalidOperationException) { Console.WriteLine("PASS effective override rejected"); passed++; }

if (args.Contains("--smoke"))
{
    // The caller must supply a verified, isolated local database. Never create, reset or migrate here.
    var effective = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var dependencies = DependencyConfiguration.Read(effective, "Testing");
    await using var db = new NpgsqlConnection(dependencies.Postgres);
    await db.OpenAsync();
    async Task<long> TableCount()
    {
        await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'public'", db);
        return (long)(await query.ExecuteScalarAsync())!;
    }
    if (await TableCount() != 0) throw new Exception("Smoke database must be empty and isolated.");
    var apiDirectory = Path.GetFullPath("EventTicketing.Api");
    foreach (var environment in new[] { "Development", "Testing" })
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = apiDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(apiDirectory, "bin/Debug/net10.0/EventTicketing.Api.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["Jwt__Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        start.Environment["BackgroundJobs__SeatHoldCleanupEnabled"] = "false";
        using var process = Process.Start(start) ?? throw new Exception("Unable to start backend.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var ready = false;
            for (var attempt = 0; attempt < 40 && !process.HasExited; attempt++)
            {
                try
                {
                    using var response = await client.GetAsync($"http://127.0.0.1:{port}/health/ready");
                    if (response.IsSuccessStatusCode) { ready = true; break; }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(250);
            }
            if (!ready) throw new Exception("Readiness failed; child output withheld to protect configuration.");
            if (await TableCount() != 0) throw new Exception("Startup unexpectedly created schema.");
            Console.WriteLine($"PASS {environment} startup + PostgreSQL SELECT 1 + Redis PING; empty schema preserved");
            passed++;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
    }
}
Console.WriteLine($"Passed {passed} environment safety checks.");
