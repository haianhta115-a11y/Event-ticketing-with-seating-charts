using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EventTicketing.Tests.Infrastructure;
using EventTicketing.Api.Data;
using Xunit;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public class AuthEndpointTests
{
    [Fact]
    public async Task Register_ValidGmail_ReturnsOk()
    {
        // Arrange
        await using var database = await LocalPostgresDatabase.CreateAsync();
        database.Context.Roles.Add(new EventTicketing.Api.Entities.Role { Id = 1, Name = "Buyer" });
        await database.Context.SaveChangesAsync();
        await using var factory = new LocalApplicationFactory(database);
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
    public async Task Register_DuplicateEmail_ReturnsSameGenericResponse_WithoutDuplicateUser()
    {
        // Arrange
        await using var database = await LocalPostgresDatabase.CreateAsync();
        database.Context.Roles.Add(new EventTicketing.Api.Entities.Role { Id = 1, Name = "Buyer" });
        await database.Context.SaveChangesAsync();
        await using var factory = new LocalApplicationFactory(database);
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(await response1.Content.ReadAsStringAsync(), content);
        await using var read = database.CreateContext();
        Assert.Equal(1, await read.Users.CountAsync(u => u.Email == "duplicate@gmail.com"));
        var user = await read.Users.SingleAsync(u => u.Email == "duplicate@gmail.com");
        Assert.Equal("Inactive", user.Status);
        Assert.Equal(1, await read.AccountActivations.CountAsync(a => a.UserId == user.Id));
    }
}
