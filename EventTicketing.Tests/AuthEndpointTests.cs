using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using EventTicketing.Api.Data;
using Xunit;

namespace EventTicketing.Tests;

public class AuthEndpointTests
{
    private WebApplicationFactory<Program> CreateTestFactory()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.IsConnected).Returns(true);

        var dbName = $"TestDb_{Guid.NewGuid()}";
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (dbDescriptor != null)
                    {
                        services.Remove(dbDescriptor);
                    }
                    services.AddDbContext<AppDbContext>(options =>
                    {
                        options.UseInMemoryDatabase(dbName);
                    });

                    var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
                    if (redisDescriptor != null)
                    {
                        services.Remove(redisDescriptor);
                    }
                    services.AddSingleton<IConnectionMultiplexer>(mockRedis.Object);
                });
            });
    }

    [Fact]
    public async Task Register_ValidGmail_ReturnsOk()
    {
        // Arrange
        using var factory = CreateTestFactory();
        var client = factory.CreateClient();
        var request = new
        {
            email = "testuser1@gmail.com",
            username = "testuser1",
            fullName = "Test User 1",
            password = "SecurePassword123!"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/auth/register", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ReturnsConflict()
    {
        // Arrange
        using var factory = CreateTestFactory();
        var client = factory.CreateClient();
        var request1 = new
        {
            email = "duplicate@gmail.com",
            username = "user1",
            fullName = "User 1",
            password = "SecurePassword123!"
        };
        var response1 = await client.PostAsJsonAsync("/api/auth/register", request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode); // Ensure first request succeeds

        var request2 = new
        {
            email = "duplicate@gmail.com",
            username = "user2",
            fullName = "User 2",
            password = "SecurePassword123!"
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/auth/register", request2);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
