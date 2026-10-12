using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EventTicketing.Api.Data;
using EventTicketing.Api.Endpoints;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using EventTicketing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                       ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

if (!string.IsNullOrEmpty(connectionString))
{
    connectionString = connectionString.Replace("${DB_HOST}", Environment.GetEnvironmentVariable("DB_HOST"))
                                       .Replace("${DB_PORT}", Environment.GetEnvironmentVariable("DB_PORT"))
                                       .Replace("${DB_NAME}", Environment.GetEnvironmentVariable("DB_NAME"))
                                       .Replace("${DB_USER}", Environment.GetEnvironmentVariable("DB_USER"))
                                       .Replace("${DB_PASSWORD}", Environment.GetEnvironmentVariable("DB_PASSWORD"));
}

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseNpgsql(connectionString);
        // Bá» qua cáº£nh bÃ¡o PendingModelChangesWarning Ä‘á»ƒ trÃ¡nh crash khi khá»Ÿi Ä‘á»™ng
        options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    });
}

var redisConnection = builder.Configuration.GetSection("Redis")["ConnectionString"]
                      ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION");

if (string.IsNullOrEmpty(redisConnection) || redisConnection == "${REDIS_CONNECTION}")
{
    redisConnection = "localhost:6379";
}
else
{
    var envRedis = Environment.GetEnvironmentVariable("REDIS_CONNECTION");
    if (!string.IsNullOrEmpty(envRedis))
    {
        redisConnection = redisConnection.Replace("${REDIS_CONNECTION}", envRedis);
    }
}

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ISeatingChartValidator, SeatingChartValidator>();
builder.Services.AddScoped<ISeatReservationService, SeatReservationService>();

var jwtKey = builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY") ?? "DayLaMotKhoaBaoMatDuDaiChoJwtToken123!";
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, CustomAuthorizationMiddlewareResultHandler>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(
                "https://event-ticketing-with-seating-charts-1.onrender.com",
                "https://event-ticketing-with-seating-charts.onrender.com",
                "http://localhost:5173",
                "http://localhost:3000"
            )
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Automatically apply migrations at startup
if (!app.Environment.IsEnvironment("Testing"))
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();

        if (app.Environment.IsDevelopment())
        {
            EventDbSeeder.Seed(db);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Migration notice: {ex.Message}");
    }
}

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
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapSeatingChartEndpoints();
app.MapSeatReservationEndpoints();
app.MapPublicEventEndpoints();

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

app.MapPost("/api/auth/register", async (
    [Microsoft.AspNetCore.Mvc.FromBody] RegisterRequest request,
    [Microsoft.AspNetCore.Mvc.FromServices] AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password) || string.IsNullOrWhiteSpace(request.Username))
    {
        return Results.Problem(statusCode: 400, title: "Validation Error", detail: "Email, username, and password are required");
    }

    var emailRegex = new System.Text.RegularExpressions.Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$");
    if (!emailRegex.IsMatch(request.Email))
    {
        return Results.Problem(statusCode: 400, title: "Validation Error", detail: "Invalid email format");
    }

    var emailLower = request.Email.Trim().ToLowerInvariant();
    var usernameLower = request.Username.Trim().ToLowerInvariant();

    var existingUser = await db.Users.SingleOrDefaultAsync(u => u.Email.ToLower() == emailLower || (u.Username != null && u.Username.ToLower() == emailLower) || (u.Username != null && u.Username.ToLower() == usernameLower));
    if (existingUser != null)
    {
        if (existingUser.Email.ToLower() == emailLower)
        {
            return Results.Problem(statusCode: 409, title: "Conflict", detail: "Email already registered");
        }
        return Results.Problem(statusCode: 409, title: "Conflict", detail: "Username already registered");
    }

    var newUser = new User
    {
        Email = emailLower,
        Username = request.Username.Trim(),
        Name = string.IsNullOrWhiteSpace(request.FullName) ? "User" : request.FullName.Trim(),
        PasswordHash = PasswordHasher.HashPassword(request.Password),
        Status = "Active",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    db.Users.Add(newUser);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (Microsoft.EntityFrameworkCore.DbUpdateException)
    {
        return Results.Problem(statusCode: 409, title: "Conflict", detail: "Email or username already registered");
    }
    var buyerRole = await db.Roles.SingleOrDefaultAsync(r => r.Name == "Buyer")
                    ?? await db.Roles.SingleOrDefaultAsync(r => r.Id == 1);
    if (buyerRole != null)
    {
        db.UserRoles.Add(new UserRole
        {
            UserId = newUser.Id,
            RoleId = buyerRole.Id
        });
        await db.SaveChangesAsync();
    }
    return Results.Ok(new { message = "Registration successful" });
})
.WithName("Register")
.AllowAnonymous();

app.MapPost("/api/auth/login", async (
    [Microsoft.AspNetCore.Mvc.FromBody] LoginRequest request,
    [Microsoft.AspNetCore.Mvc.FromServices] AppDbContext db,
    [Microsoft.AspNetCore.Mvc.FromServices] IConnectionMultiplexer redis) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
    {
        return Results.Json(new { message = "Invalid email or password" }, statusCode: 401);
    }

    var redisDb = redis.GetDatabase();
    var emailLower = request.Email.Trim().ToLowerInvariant();
    var lockKey = $"lockout:{emailLower}";
    var attemptsKey = $"attempts:{emailLower}";

    // Check lockout status
    var lockTimeRemaining = await redisDb.KeyTimeToLiveAsync(lockKey);
    if (lockTimeRemaining.HasValue && lockTimeRemaining.Value.TotalSeconds > 0)
    {
        return Results.Json(new { message = "Account locked for 15 minutes" }, statusCode: 403);
    }

    var user = await db.Users
        .Include(u => u.UserRoles)
        .ThenInclude(ur => ur.Role)
        .SingleOrDefaultAsync(u => u.Email.ToLower() == emailLower || (u.Username != null && u.Username.ToLower() == emailLower));

    if (user == null)
    {
        return Results.Json(new { message = "Invalid email or password" }, statusCode: 401);
    }

    if (user.Status == "Inactive")
    {
        return Results.Json(new { error = new { code = "ACCOUNT_INACTIVE", message = "Account not activated" } }, statusCode: 403);
    }

    if (!PasswordHasher.VerifyPassword(user.PasswordHash, request.Password))
    {
        var attempts = await redisDb.StringIncrementAsync(attemptsKey);
        if (attempts == 1)
        {
            await redisDb.KeyExpireAsync(attemptsKey, TimeSpan.FromMinutes(15));
        }

        if (attempts >= 5)
        {
            await redisDb.StringSetAsync(lockKey, "locked", TimeSpan.FromMinutes(15));
            await redisDb.KeyDeleteAsync(attemptsKey);
        }

        return Results.Json(new { message = "Invalid email or password" }, statusCode: 401);
    }

    // On successful login, clear attempts
    await redisDb.KeyDeleteAsync(attemptsKey);

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

    if (userRoles.Count == 0)
    {
        userRoles.Add("Buyer");
    }

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




