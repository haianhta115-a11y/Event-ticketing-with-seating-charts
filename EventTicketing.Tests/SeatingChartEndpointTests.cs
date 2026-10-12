using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using EventTicketing.Api.Data;
using EventTicketing.Api.Models.SeatingChart;
using Xunit;

namespace EventTicketing.Tests;

public class SeatingChartEndpointTests
{
    private WebApplicationFactory<Program> CreateTestFactory()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.IsConnected).Returns(true);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (dbDescriptor != null) services.Remove(dbDescriptor);

                    services.AddDbContext<AppDbContext>(options =>
                    {
                        options.UseSqlite("DataSource=:memory:");
                    });

                    var redisDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
                    if (redisDescriptor != null) services.Remove(redisDescriptor);

                    services.AddSingleton<IConnectionMultiplexer>(mockRedis.Object);
                });
            });
    }

    [Fact]
    public async Task ValidateEndpoint_WhenValidJson_Returns200OK_WithValidationSummary()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var validJson = @"
        {
            ""layoutName"": ""Nhà hát Tuổi Trẻ"",
            ""venue"": ""Hà Nội"",
            ""totalCapacity"": 1,
            ""sections"": [
                {
                    ""name"": ""Khán phòng 1"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 150000, ""status"": ""Available"" }
                            ]
                        }
                    ]
                }
            ]
        }";

        var content = new StringContent(validJson, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/seating-charts/validate", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var isValid = doc.RootElement.GetProperty("isValid").GetBoolean();
        Assert.True(isValid);
        Assert.Equal(1, doc.RootElement.GetProperty("totalSeats").GetInt32());
    }

    [Fact]
    public async Task ValidateEndpoint_WhenInvalidSeatPrice_Returns400BadRequest_AndRejectsEntirely()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var invalidJson = @"
        {
            ""layoutName"": ""Nhà hát Lớn"",
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": -200000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        var content = new StringContent(invalidJson, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/seating-charts/validate", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var isValid = doc.RootElement.GetProperty("isValid").GetBoolean();
        Assert.False(isValid);

        var errors = doc.RootElement.GetProperty("errors");
        Assert.True(errors.GetArrayLength() > 0);
        var firstError = errors[0];
        Assert.Equal("NEGATIVE_PRICE", firstError.GetProperty("errorCode").GetString());
        Assert.Contains("VIP", firstError.GetProperty("location").GetString());
    }

    [Fact]
    public async Task UploadEndpoint_WhenInvalidLayout_RejectsEntireFile_Returns400BadRequest()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var invalidJson = @"
        {
            ""layoutName"": ""Sân vận động Mỹ Đình"",
            ""sections"": [
                {
                    ""name"": ""Khu A"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""1"",
                            ""seats"": [
                                { ""seatNumber"": ""10"", ""price"": 100000 },
                                { ""seatNumber"": ""10"", ""price"": 100000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        var content = new StringContent(invalidJson, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/seating-charts/upload", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("isValid").GetBoolean());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Contains("DUPLICATE_SEAT", errors.ToString());
    }

    [Fact]
    public async Task UploadEndpoint_WhenValidLayout_Returns201Created()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var validJson = @"
        {
            ""layoutName"": ""Nhà thi đấu Quân Khu 7"",
            ""venue"": ""TP. Hồ Chí Minh"",
            ""totalCapacity"": 2,
            ""sections"": [
                {
                    ""name"": ""Khán đài A"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""1"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 100000, ""x"": 1, ""y"": 1 },
                                { ""seatNumber"": ""2"", ""price"": 100000, ""x"": 2, ""y"": 1 }
                            ]
                        }
                    ]
                }
            ]
        }";

        var content = new StringContent(validJson, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/seating-charts/upload", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task UploadEndpoint_WithMultipartJsonFile_WhenValid_Returns201Created()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var validJson = @"
        {
            ""layoutName"": ""Cung Điền Kinh"",
            ""sections"": [
                {
                    ""name"": ""Zone 1"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 50000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(validJson));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        form.Add(fileContent, "file", "chart_layout.json");

        var response = await client.PostAsync("/api/seating-charts/upload", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UploadEndpoint_WithMultipartNonJsonFile_Returns400BadRequest()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("plain text file"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        form.Add(fileContent, "file", "chart_layout.txt");

        var response = await client.PostAsync("/api/seating-charts/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_FILE_EXTENSION", body);
    }

    [Fact]
    public async Task TemplateEndpoint_Returns200OK_WithValidSeatingChartTemplate()
    {
        using var factory = CreateTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/seating-charts/template");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<SeatingChartFileDto>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto.LayoutName));
        Assert.NotNull(dto.Sections);
        Assert.NotEmpty(dto.Sections);
    }
}

