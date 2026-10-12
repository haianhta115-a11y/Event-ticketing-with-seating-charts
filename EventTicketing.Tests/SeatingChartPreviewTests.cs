using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTicketing.Api.Entities;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class SeatingChartPreviewTests : IAsyncLifetime
{
    private LocalPostgresDatabase database = null!;
    private LocalApplicationFactory factory = null!;
    private HttpClient client = null!;
    private readonly PreviewReadOnlyGuard readOnly = new();
    private const string Valid = """{"layoutName":"Preview","totalCapacity":3,"seats":[{"section":"Main","row":"A","seatNumber":"1","category":"VIP","x":1,"y":1},{"section":"Main","row":"a","seatNumber":"2","category":"vip","x":2,"y":1},{"section":"Main","row":"B","seatNumber":"1","category":"Standard","x":1,"y":2}]}""";
    private async Task<HttpResponseMessage> Send(string route = "preview", string json = Valid, string filename = "chart.json", int showtime = 10)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "file", filename);
        return await client.PostAsync($"/api/showtimes/{showtime}/seats/{route}", form);
    }
    public async Task InitializeAsync()
    {
        database = await LocalPostgresDatabase.CreateAsync();
        database.Context.Users.Add(new User { Id = 201, Email = "preview@example.test", Username = "preview", Name = "Fixture", PasswordHash = "unused", Status = "Active" });
        await LocalPostgresDatabase.EnsureShowtimeAsync(database.Context, 10);
        (await database.Context.Showtimes.Include(s => s.Event).SingleAsync()).Event.OwnerUserId = 201;
        await database.Context.SaveChangesAsync();
        factory = new LocalApplicationFactory(database) { DbInterceptors = [readOnly] };
        client = factory.CreateClient(); factory.Authenticate(client, 201, "Organizer");
    }

    [Fact]
    public async Task Preview_ProducesCanonicalGridAndSummary_WithoutAnyDatabaseWrite()
    {
        readOnly.Enabled = true;
        var response = await Send(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("persisted").GetBoolean());
        Assert.Equal(3, body.GetProperty("validation").GetProperty("totalSeats").GetInt32());
        Assert.Equal(2, body.GetProperty("validation").GetProperty("totalRows").GetInt32());
        var seats = body.GetProperty("seats").EnumerateArray().ToArray();
        Assert.Equal(3, seats.Length); Assert.Equal("A", seats[1].GetProperty("row").GetString());
        Assert.Equal("VIP", seats[1].GetProperty("category").GetString());
        Assert.Equal(2, seats[1].GetProperty("x").GetInt32());
        using var db = database.CreateContext(); Assert.Empty(await db.Seats.ToListAsync()); Assert.Empty(await db.SeatCategories.ToListAsync());
        Assert.Equal(0, readOnly.AttemptedWrites);
    }

    [Theory]
    [InlineData("Anonymous", 401)]
    [InlineData("Buyer", 403)]
    [InlineData("OtherOrganizer", 403)]
    [InlineData("GateStaff", 403)]
    [InlineData("Admin", 200)]
    public async Task Preview_EnforcesRolesAndOwnership(string role, int expected)
    {
        if (role == "Anonymous") client.DefaultRequestHeaders.Authorization = null;
        else factory.Authenticate(client, role == "OtherOrganizer" ? 202 : 201, role == "OtherOrganizer" ? "Organizer" : role);
        Assert.Equal(expected, (int)(await Send()).StatusCode);
        using var db = database.CreateContext(); Assert.Empty(await db.Seats.ToListAsync());
    }

    [Fact]
    public async Task Preview_ForMissingShowtime_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(showtime: 999)).StatusCode);
    }

    [Fact]
    public async Task ValidationPreviewAndImport_ReturnIdenticalCompleteErrors_PreserveExistingMap()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send("import")).StatusCode);
        using var db = database.CreateContext();
        var before = await db.Seats.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.Row, s.Number, s.SeatCategoryId }).ToListAsync();
        const string broken = """{"layoutName":"Broken","seats":[{"section":"S","row":"A","seatNumber":"1","x":"bad"},{"section":"Other","row":"A","seatNumber":"1","category":""},{"section":"S","row":"C","category":"VIP","y":-1}]}""";
        readOnly.Enabled = true;
        var preview = await Send(json: broken); var import = await Send("import", broken);
        var validate = await client.PostAsync("/api/seating-charts/validate", new StringContent(broken, Encoding.UTF8, "application/json"));
        foreach (var response in new[] { preview, import, validate }) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var previewBody = await preview.Content.ReadAsStringAsync();
        Assert.Equal(previewBody, await import.Content.ReadAsStringAsync());
        Assert.Equal(previewBody, await validate.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(previewBody).RootElement;
        var paths = body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToArray();
        foreach (var path in new[] { "seats[0].x", "seats[1].seatNumber", "seats[1].category", "seats[2].seatNumber", "seats[2].y" }) Assert.Contains(path, paths);
        var after = await db.Seats.AsNoTracking().OrderBy(s => s.Id).Select(s => new { s.Id, s.Row, s.Number, s.SeatCategoryId }).ToListAsync();
        Assert.Equal(before, after); Assert.Equal(0, readOnly.AttemptedWrites);
    }

    [Fact]
    public async Task Confirmation_RevalidatesCurrentState_RejectsHoldCreatedAfterPreview()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send("import")).StatusCode);
        readOnly.Enabled = true;
        Assert.Equal(HttpStatusCode.OK, (await Send()).StatusCode);
        readOnly.Enabled = false;
        using var db = database.CreateContext(); var seat = await db.Seats.OrderBy(s => s.Id).FirstAsync();
        seat.Status = SeatStatus.Held; seat.HeldByUserId = 201; seat.HeldUntil = DateTime.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await Send("import")).StatusCode);
        db.ChangeTracker.Clear(); Assert.Equal(SeatStatus.Held, (await db.Seats.SingleAsync(s => s.Id == seat.Id)).Status);
        Assert.Equal(3, await db.Seats.CountAsync());
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("import")]
    public async Task OversizedFile_Is413_NoWrites(string route)
    {
        readOnly.Enabled = true;
        var response = await Send(route, new string(' ', SeatingChartPreparation.MaximumBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FILE_TOO_LARGE", body.GetProperty("errors")[0].GetProperty("errorCode").GetString());
        Assert.Equal(0, readOnly.AttemptedWrites);
    }

    [Theory]
    [InlineData("preview", "{", "chart.json", "INVALID_JSON_SYNTAX")]
    [InlineData("import", "{", "chart.json", "INVALID_JSON_SYNTAX")]
    [InlineData("preview", "{}", "chart.txt", "INVALID_FILE_EXTENSION")]
    public async Task MalformedInput_Is400Structured_Not500(string route, string json, string filename, string code)
    {
        readOnly.Enabled = true; var response = await Send(route, json, filename);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("errors")[0].GetProperty("errorCode").GetString());
        Assert.Equal(0, readOnly.AttemptedWrites);
    }

    [Fact]
    public async Task LegacyRawValidate_Enforces5MbLimit()
    {
        var response = await client.PostAsync("/api/seating-charts/validate", new StringContent(new string(' ', SeatingChartPreparation.MaximumBytes + 1), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    public async Task DisposeAsync() { client.Dispose(); await factory.DisposeAsync(); await database.DisposeAsync(); }

    private sealed class PreviewReadOnlyGuard : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int AttemptedWrites { get; private set; }
        private void Check(DbCommand command)
        {
            if (Enabled && !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            { AttemptedWrites++; throw new InvalidOperationException("Preview attempted a database write."); }
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        { Check(command); return ValueTask.FromResult(result); }
    }
}
