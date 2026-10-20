using EventTicketing.Api.Email;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EventTicketing.Api.Data;
using EventTicketing.Api.Endpoints;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.EntityFrameworkCore;
using EventTicketing.Api.Configuration;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Servers = [
            new() { Url = "/" }
        ];

        return Task.CompletedTask;
    });
});

// Validate the effective configuration after JSON, environment and command-line overrides.
var dependencies = DependencyConfiguration.Read(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddSingleton(dependencies);
builder.Services.AddSingleton<RedisKeyScope>();
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dependencies.Postgres));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(dependencies.Redis));
builder.Services.AddHealthChecks().AddCheck<DependencyHealthCheck>("dependencies", tags: ["ready"]);
builder.Services.AddControllers().AddJsonOptions(options => { options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()); });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IReservationClock, PostgresReservationClock>();
builder.Services.AddScoped<ISeatingChartValidator, SeatingChartValidator>();
builder.Services.AddScoped<EventTicketing.Api.SeatMaps.SeatMapImportService>();
builder.Services.AddScoped<EventTicketing.Api.SeatMaps.SeatingChartPreparation>();
builder.Services.AddScoped<ISeatReservationService, SeatReservationService>();
builder.Services.AddOptions<SeatHoldOptions>()
    .Bind(builder.Configuration.GetSection(SeatHoldOptions.SectionName))
    .Validate(options => options.DurationMinutes is > 0 and <= 1440,
        "SeatHolds:DurationMinutes must be between 1 and 1440 minutes.")
    .ValidateOnStart();
builder.Services.AddScoped<SalesStatusService>();
builder.Services.AddScoped<ISeatPricingService, SeatPricingService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddOptions<OrderOptions>()
    .Bind(builder.Configuration.GetSection(OrderOptions.SectionName))
    .Validate(options => options.PaymentTimeoutMinutes is > 0 and <= 1440,
        "Orders:PaymentTimeoutMinutes must be between 1 and 1440 minutes.")
    .ValidateOnStart();
builder.Services.AddScoped<IPaymentService, PaymentService>();
PaymentGatewayConfiguration.ConfigurePaymentGateway(builder.Services, builder.Configuration, builder.Environment);
builder.Services.AddScoped<ExpiredSeatHoldCleanup>();
builder.Services.AddScoped<ExpiredOrderCleanup>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddActivationEmail(builder.Configuration, builder.Environment);
// No background database writes by default in local development or tests.
var localEnvironment = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
if (builder.Configuration.GetValue<bool?>("BackgroundJobs:SeatHoldCleanupEnabled") ?? !localEnvironment)
    builder.Services.AddHostedService<EventTicketing.Api.BackgroundServices.ExpiredSeatHoldCleanupService>();
if (builder.Configuration.GetValue<bool?>("BackgroundJobs:OrderCleanupEnabled") ?? !localEnvironment)
    builder.Services.AddHostedService<EventTicketing.Api.BackgroundServices.ExpiredOrderCleanupService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? builder.Configuration["JWT_KEY"];
if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException("Configure Jwt__Key or JWT_KEY with at least 32 bytes; no default signing key is provided.");
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, EventOwnerHandler>();
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (AccessPolicies.UserId(context.Principal!) == null)
                    context.Fail("Invalid user identity.");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(_ => false)
        .Build();
    options.DefaultPolicy = options.FallbackPolicy;
    options.AddPolicy(AccessPolicies.EventManagement, policy => policy.RequireAuthenticatedUser().RequireRole("Organizer", "Admin"));
    options.AddPolicy(AccessPolicies.SeatBooking, policy => policy.RequireAuthenticatedUser().RequireRole("Buyer"));
    options.AddPolicy(AccessPolicies.EventOwner, policy => policy.RequireAuthenticatedUser().AddRequirements(new EventOwnerRequirement()));
});

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, CustomAuthorizationMiddlewareResultHandler>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [
                "https://event-ticketing-with-seating-charts-1.onrender.com",
                "https://event-ticketing-with-seating-charts.onrender.com",
                "http://localhost:5173",
                "http://localhost:3000"
            ];
        policy
            .WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Schema changes and data initialization are explicit operations, never startup side effects.

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("Frontend");
app.UseAuthentication();
// Public browsing stays anonymous when no Bearer credential is supplied. An
// explicitly supplied invalid/expired credential must not fall back to anonymous
// access, even on AllowAnonymous endpoints (S-02.4).
app.Use(async (context, next) =>
{
    var authorization = context.Request.Headers.Authorization;
    var suppliedBearer = authorization.Any(value => value != null
        && value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase)
        && (value.Length == 6 || char.IsWhiteSpace(value[6])));
    if (suppliedBearer)
    {
        var result = await context.AuthenticateAsync(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
        if (authorization.Count != 1 || !result.Succeeded)
        {
            await context.ChallengeAsync(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
            return;
        }
    }
    await next(context);
});
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapSeatingChartEndpoints();
app.MapSeatReservationEndpoints();
app.MapPublicEventEndpoints();
app.MapControllers();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");


app.MapRegistrationEndpoints();

app.MapPost("/api/auth/login", async (
    [Microsoft.AspNetCore.Mvc.FromBody] LoginRequest request,
    [Microsoft.AspNetCore.Mvc.FromServices] AppDbContext db,
    [Microsoft.AspNetCore.Mvc.FromServices] LoginAttemptLimiter limiter,
    HttpContext httpContext) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
    {
        return Results.Json(new { message = "Invalid email or password" }, statusCode: 401);
    }

    var emailLower = request.Email.Trim().ToLowerInvariant();
    // Use the peer address, never an untrusted client-supplied IP/header.
    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var user = await db.Users
        .Include(u => u.UserRoles)
        .ThenInclude(ur => ur.Role)
        .SingleOrDefaultAsync(u => u.Email.ToLower() == emailLower || (u.Username != null && u.Username.ToLower() == emailLower));

    var accountKey = user?.Email.Trim().ToLowerInvariant() ?? emailLower;
    var remaining = await limiter.RemainingSecondsAsync(accountKey, ip);
    if (remaining > 0)
        return Results.Json(new { message = "Account locked for 15 minutes", remainingSeconds = remaining }, statusCode: 403);

    if (!PasswordHasher.VerifyPasswordOrDummy(user?.PasswordHash, request.Password) || user == null)
    {
        await limiter.RecordFailureAsync(accountKey, ip);
        return Results.Json(new { message = "Invalid email or password" }, statusCode: 401);
    }

    if (user.Status != "Active")
        return Results.Json(new { error = new { code = "ACCOUNT_INACTIVE", message = "Account not activated or disabled" } }, statusCode: 403);

    remaining = await limiter.CompleteLoginAsync(accountKey, ip);
    if (remaining > 0)
        return Results.Json(new { message = "Account locked for 15 minutes", remainingSeconds = remaining }, statusCode: 403);

    var keyBytes = Encoding.UTF8.GetBytes(jwtKey);
    var securityKey = new SymmetricSecurityKey(keyBytes);
    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

    var claims = new List<Claim>
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email)
    };

    var userRoles = user.UserRoles
        .Select(ur => ur.Role?.Name)
        .Where(name => !string.IsNullOrEmpty(name))
        .ToList();

    foreach (var roleName in userRoles)
    {
        claims.Add(new Claim(ClaimTypes.Role, roleName!));
    }

    var tokenDescriptor = new JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddHours(2),
        signingCredentials: credentials);

    var tokenString = new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);

    return Results.Ok(new
    {
        message = "Login successful",
        userId = user.Id,
        roles = userRoles,
        token = tokenString
    });
})
.WithName("Login")
.AllowAnonymous();
app.Run();

public record RegisterRequest(string Email, string Username, string FullName, string Password);

public record LoginRequest(string Email, string Password);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

public partial class Program { }
