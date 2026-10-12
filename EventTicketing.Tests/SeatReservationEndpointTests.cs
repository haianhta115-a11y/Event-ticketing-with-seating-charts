using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;

namespace EventTicketing.Tests;

public class SeatReservationEndpointTests : IAsyncDisposable
{
    private readonly SqliteConnection sqliteConnection;
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public SeatReservationEndpointTests()
    {
        sqliteConnection = new SqliteConnection("Data Source=:memory:");
        sqliteConnection.Open();

        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.IsConnected).Returns(true);

        factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.AddDbContext<AppDbContext>(options =>
                    {
                        options.UseSqlite(sqliteConnection);
                    });

                    var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
                    if (redisDescriptor != null) services.Remove(redisDescriptor);

                    services.AddSingleton<IConnectionMultiplexer>(mockRedis.Object);
                });
            });

        // Khởi tạo schema trong SQLite
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();

        client = factory.CreateClient();
    }

    [Fact]
    public async Task HoldEndpoint_WhenSeatAvailable_Returns200OK_WithCountdown10Minutes()
    {
        int seatId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cat = new SeatCategory { PerformanceId = 1, Name = "VIP", NormalizedName = "VIP" };
            db.SeatCategories.Add(cat);
            await db.SaveChangesAsync();

            var seat = new Seat { PerformanceId = 1, SeatCategoryId = cat.Id, Row = "A", Number = "1", Status = SeatStatus.Available };
            db.Seats.Add(seat);
            await db.SaveChangesAsync();
            seatId = seat.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/performances/1/seats/{seatId}/hold", new { UserId = 101 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(seatId, body.GetProperty("seatId").GetInt32());
        Assert.Equal("Held", body.GetProperty("status").GetString());
        Assert.Equal(600, body.GetProperty("remainingSeconds").GetInt32());
        Assert.True(body.TryGetProperty("serverTimeUtc", out _));
        Assert.True(body.TryGetProperty("heldUntilUtc", out _));
    }

    [Fact]
    public async Task HoldEndpoint_WhenSeatHeldByAnotherUser_Returns409Conflict_WithCorrectMessage()
    {
        int seatId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cat = new SeatCategory { PerformanceId = 1, Name = "Standard", NormalizedName = "STANDARD" };
            db.SeatCategories.Add(cat);
            await db.SaveChangesAsync();

            var seat = new Seat { PerformanceId = 1, SeatCategoryId = cat.Id, Row = "B", Number = "2", Status = SeatStatus.Available };
            db.Seats.Add(seat);
            await db.SaveChangesAsync();
            seatId = seat.Id;
        }

        // User A giữ trước
        var firstResponse = await client.PostAsJsonAsync($"/api/performances/1/seats/{seatId}/hold", new { UserId = 101 });
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // User B thử giữ
        var secondResponse = await client.PostAsJsonAsync($"/api/performances/1/seats/{seatId}/hold", new { UserId = 102 });

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        var body = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("ghế vừa có người chọn", body.GetProperty("message").GetString());
        Assert.Equal("SEAT_ALREADY_HELD", body.GetProperty("errorCode").GetString());
        Assert.Equal("Held", body.GetProperty("currentSeat").GetProperty("status").GetString());
    }

    [Fact]
    public async Task HoldEndpoint_WhenPerformanceSalesClosed_Returns400BadRequest_WithSalesClosedMessage()
    {
        int seatId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var perf = new Performance { Id = 88, Title = "Suất diễn đóng", IsSalesClosed = true };
            db.Performances.Add(perf);

            var cat = new SeatCategory { PerformanceId = 88, Name = "VIP", NormalizedName = "VIP" };
            db.SeatCategories.Add(cat);
            await db.SaveChangesAsync();

            var seat = new Seat { PerformanceId = 88, SeatCategoryId = cat.Id, Row = "C", Number = "3", Status = SeatStatus.Available };
            db.Seats.Add(seat);
            await db.SaveChangesAsync();
            seatId = seat.Id;
        }

        var response = await client.PostAsJsonAsync($"/api/performances/88/seats/{seatId}/hold", new { UserId = 101 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("suất không còn mở bán", body.GetProperty("message").GetString());
        Assert.Equal("SALES_CLOSED", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ReleaseEndpoint_FreesHeldSeat_Returns200OK()
    {
        int seatId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cat = new SeatCategory { PerformanceId = 1, Name = "Standard", NormalizedName = "STANDARD" };
            db.SeatCategories.Add(cat);
            await db.SaveChangesAsync();

            var seat = new Seat { PerformanceId = 1, SeatCategoryId = cat.Id, Row = "D", Number = "4", Status = SeatStatus.Available };
            db.Seats.Add(seat);
            await db.SaveChangesAsync();
            seatId = seat.Id;
        }

        // Giữ ghế
        await client.PostAsJsonAsync($"/api/performances/1/seats/{seatId}/hold", new { UserId = 101 });

        // Hủy giữ ghế
        var deleteResponse = await client.DeleteAsync($"/api/performances/1/seats/{seatId}/hold?userIdQuery=101");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Kiểm tra sơ đồ ghế
        var chartResponse = await client.GetAsync("/api/performances/1/seats");
        Assert.Equal(HttpStatusCode.OK, chartResponse.StatusCode);
        var chartBody = await chartResponse.Content.ReadFromJsonAsync<JsonElement>();
        var seatsArray = chartBody.GetProperty("seats").EnumerateArray();
        var freedSeat = seatsArray.First(s => s.GetProperty("id").GetInt32() == seatId);
        Assert.Equal("Available", freedSeat.GetProperty("displayStatus").GetString());
    }

    [Fact]
    public async Task ReleaseEndpoint_AC3_WhenHeldByAnotherUser_Returns403Forbidden_LeavesSeatIntact()
    {
        int seatId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cat = new SeatCategory { PerformanceId = 1, Name = "Standard", NormalizedName = "STANDARD" };
            db.SeatCategories.Add(cat);
            await db.SaveChangesAsync();

            var seat = new Seat { PerformanceId = 1, SeatCategoryId = cat.Id, Row = "E", Number = "5", Status = SeatStatus.Available };
            db.Seats.Add(seat);
            await db.SaveChangesAsync();
            seatId = seat.Id;
        }

        // User A (101) giữ ghế
        await client.PostAsJsonAsync($"/api/performances/1/seats/{seatId}/hold", new { UserId = 101 });

        // User B (102) thử hủy ghế của User A
        var deleteResponse = await client.DeleteAsync($"/api/performances/1/seats/{seatId}/hold?userIdQuery=102");
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);

        var body = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Ghế đang được giữ bởi người khác.", body.GetProperty("message").GetString());

        // Kiểm tra ghế vẫn còn được giữ bởi User A
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var checkSeat = await db.Seats.FindAsync(seatId);
            Assert.Equal(SeatStatus.Held, checkSeat!.Status);
            Assert.Equal(101, checkSeat.HeldByUserId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        await sqliteConnection.DisposeAsync();
    }
}

