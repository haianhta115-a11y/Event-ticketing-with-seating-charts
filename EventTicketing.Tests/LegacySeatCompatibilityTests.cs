using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Models.SeatingChart;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Api.Services;
using EventTicketing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EventTicketing.Tests;

[Trait("Category", "Integration")]
public sealed class LegacySeatCompatibilityTests
{
    private static async Task<LocalPostgresDatabase> ExpandedLegacyAsync(bool requiredPerformance = true)
    {
        var fixture = await LocalPostgresDatabase.CreateAsync();
        await LocalPostgresDatabase.EnsureShowtimeAsync(fixture.Context, 10);
        await fixture.Context.Database.ExecuteSqlRawAsync("""
            ALTER TABLE seats ALTER COLUMN showtime_id DROP NOT NULL;
            ALTER TABLE seat_categories ALTER COLUMN showtime_id DROP NOT NULL;
            ALTER TABLE seats ADD COLUMN performance_id integer;
            ALTER TABLE seat_categories ADD COLUMN performance_id integer;
            ALTER TABLE seat_categories ADD CONSTRAINT legacy_category_key UNIQUE(performance_id,id);
            ALTER TABLE seats ADD CONSTRAINT legacy_category_fk FOREIGN KEY(performance_id,seat_category_id)
                REFERENCES seat_categories(performance_id,id);
            INSERT INTO seat_categories(id,performance_id,showtime_id,name,normalized_name)
                VALUES(8888,385,NULL,'Legacy','LEGACY'),(9999,337,NULL,'Legacy','LEGACY'),(20,900,10,'Mapped','MAPPED');
            INSERT INTO seats(id,performance_id,showtime_id,seat_category_id,"row",number,status,x,y)
                VALUES(8888,385,NULL,8888,'L','1','AVAILABLE',NULL,NULL),
                      (9999,337,NULL,9999,'L','2','SOLD',NULL,NULL),
                      (20,900,10,20,'A','1','AVAILABLE',NULL,NULL);
            """);
        if (requiredPerformance)
            await fixture.Context.Database.ExecuteSqlRawAsync("ALTER TABLE seats ALTER COLUMN performance_id SET NOT NULL; ALTER TABLE seat_categories ALTER COLUMN performance_id SET NOT NULL;");
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static SeatMapImportDocument Map(int? x = null, int? y = null) => new()
    { Seats = [new SeatMapImportItem { Row = "A", Number = "1", Category = "Mapped", X = x, Y = y }] };

    [Fact]
    public async Task ActiveQueries_ExcludeUnresolvedKeys_WhileReadProjectionPreservesAllIdsAndNulls()
    {
        await using var f = await ExpandedLegacyAsync();
        var seat = Assert.Single(await f.Context.Seats.Include(s => s.SeatCategory).ToListAsync());
        Assert.Equal(20, seat.Id); Assert.Null(seat.X); Assert.Null(seat.Y);
        Assert.Equal(20, Assert.Single(await f.Context.SeatCategories.ToListAsync()).Id);
        var legacy = await LegacySeatData.ReadSeats(f.Context).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(new[] { 20, 8888, 9999 }, legacy.Select(s => s.Id));
        Assert.All(legacy.Where(s => s.Id != 20), s => { Assert.Null(s.ShowtimeId); Assert.Null(s.X); Assert.Null(s.Y); });
        Assert.Equal(385, legacy.Single(s => s.Id == 8888).PerformanceId);
        Assert.Equal(337, legacy.Single(s => s.Id == 9999).PerformanceId);
        Assert.Equal(2, await LegacySeatData.ReadCategories(f.Context).CountAsync(c => c.ShowtimeId == null));
        Assert.DoesNotContain(f.Context.ChangeTracker.Entries(), e => e.Entity is LegacySeatRecord or LegacyCategoryRecord);
    }

    [Fact]
    public async Task LegacyOnlySchema_HasSafeNullableReadProjection()
    {
        await using var f = await LocalPostgresDatabase.CreateAsync();
        // Simulate pre-expand schema only in the new fixture. Original databases are never used.
        await f.Context.Database.ExecuteSqlRawAsync("ALTER TABLE seats RENAME COLUMN showtime_id TO performance_id; ALTER TABLE seat_categories RENAME COLUMN showtime_id TO performance_id; ALTER TABLE seats DROP COLUMN x; ALTER TABLE seats DROP COLUMN y;");
        Assert.Empty(await LegacySeatData.ReadSeats(f.Context).ToListAsync());
        using var factory = new LocalApplicationFactory(f);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/showtimes/10/seats/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("SEAT_SCHEMA_NOT_READY", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("SELECT", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PublicMap_ExcludesUnmapped_AndSerializesUnknownGeometryAsNull()
    {
        await using var f = await ExpandedLegacyAsync();
        using var factory = new LocalApplicationFactory(f);
        using var client = factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/showtimes/10/seats/");
        var seat = Assert.Single(body.GetProperty("seats").EnumerateArray());
        Assert.Equal(20, seat.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, seat.GetProperty("x").ValueKind);
        Assert.Equal(JsonValueKind.Null, seat.GetProperty("y").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnresolvedReservation_IsControlled_AndDoesNotMutate(bool release)
    {
        await using var f = await ExpandedLegacyAsync();
        using var factory = new LocalApplicationFactory(f);
        using var client = factory.CreateClient();
        factory.Authenticate(client, 201, "Buyer");
        var response = release ? await client.DeleteAsync("/api/showtimes/10/seats/8888/hold")
            : await client.PostAsJsonAsync("/api/showtimes/10/seats/8888/hold", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("SEAT_DATA_UNRESOLVED", await response.Content.ReadAsStringAsync());
        Assert.Null((await LegacySeatData.ReadSeats(f.Context).SingleAsync(s => s.Id == 8888)).ShowtimeId);
        Assert.Equal(2, await f.Context.Seats.IgnoreQueryFilters().CountAsync(s => s.Id == 8888 && s.Status == SeatStatus.Available || s.Id == 9999 && s.Status == SeatStatus.Sold));
    }

    [Fact]
    public async Task Import_WithRequiredLegacyPerformance_FailsBeforeRowsOrSequencesChange()
    {
        await using var f = await ExpandedLegacyAsync();
        var before = await Snapshot(f.Context);
        await Assert.ThrowsAsync<SeatSchemaUnsupportedException>(() => new SeatMapImportService(f.Context, TimeProvider.System).ImportAsync(10, Map(0, 0)));
        Assert.Equal(before, await Snapshot(f.Context));
    }

    [Fact]
    public async Task PreviewRemainsReadOnly_Import503_AndOwnerAuthorizationStillApplies()
    {
        await using var f = await ExpandedLegacyAsync();
        using var factory = new LocalApplicationFactory(f);
        using var client = factory.CreateClient();
        factory.Authenticate(client, 201, "Admin");
        var before = await Snapshot(f.Context);
        async Task<HttpResponseMessage> Upload(string action)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("""{"layoutName":"Unknown geometry","seats":[{"section":"Main","row":"A","seatNumber":"1","category":"Mapped"}]}""", Encoding.UTF8, "application/json"), "file", "map.json");
            return await client.PostAsync($"/api/showtimes/10/seats/{action}", form);
        }
        var preview = await Upload("preview"); Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var json = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, json.GetProperty("seats")[0].GetProperty("x").ValueKind);
        var import = await Upload("import"); Assert.Equal(HttpStatusCode.ServiceUnavailable, import.StatusCode);
        var message = await import.Content.ReadAsStringAsync(); Assert.Contains("SEAT_SCHEMA_NOT_READY", message);
        Assert.DoesNotContain("performance_id", message); Assert.DoesNotContain("SELECT", message);
        factory.Authenticate(client, 201, "Organizer"); Assert.Equal(HttpStatusCode.Forbidden, (await Upload("import")).StatusCode);
        Assert.Equal(before, await Snapshot(f.Context));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(3, 4)]
    public async Task CompatibleWriter_PreservesUnknownOrExplicitCoordinates_AndLegacyRows(int? x, int? y)
    {
        await using var f = await ExpandedLegacyAsync(false);
        var legacyBefore = await LegacySeatData.ReadSeats(f.Context).Where(s => s.ShowtimeId == null).OrderBy(s => s.Id).ToListAsync();
        await new SeatMapImportService(f.Context, TimeProvider.System).ImportAsync(10, Map(x, y));
        f.Context.ChangeTracker.Clear();
        var seat = await f.Context.Seats.SingleAsync(); Assert.Equal(20, seat.Id); Assert.Equal(x, seat.X); Assert.Equal(y, seat.Y);
        Assert.Equal(JsonSerializer.Serialize(legacyBefore), JsonSerializer.Serialize(await LegacySeatData.ReadSeats(f.Context).Where(s => s.ShowtimeId == null).OrderBy(s => s.Id).ToListAsync()));
    }

    [Theory]
    [InlineData(SeatStatus.Sold)]
    [InlineData(SeatStatus.Held)]
    public async Task MappedNullGeometry_DoesNotBypassSoldOrActiveHoldProtection(SeatStatus status)
    {
        await using var f = await ExpandedLegacyAsync(false);
        var seat = await f.Context.Seats.SingleAsync(); seat.Status = status;
        if (status == SeatStatus.Held) { seat.HeldByUserId = 201; seat.HeldUntil = DateTime.UtcNow.AddMinutes(10); }
        await f.Context.SaveChangesAsync();
        var before = await Snapshot(f.Context);
        await Assert.ThrowsAsync<SeatMapReplacementBlockedException>(() => new SeatMapImportService(f.Context, TimeProvider.System).ImportAsync(10, Map()));
        Assert.Equal(before, await Snapshot(f.Context));
    }

    [Fact]
    public async Task NewSeatInsert_PersistsNullGeometry_WithoutDefaultOrMappingFabrication()
    {
        await using var f = await LocalPostgresDatabase.CreateAsync();
        await LocalPostgresDatabase.EnsureShowtimeAsync(f.Context, 10);
        await new SeatMapImportService(f.Context, TimeProvider.System).ImportAsync(10, Map());
        var seat = await f.Context.Seats.SingleAsync();
        Assert.Null(seat.X); Assert.Null(seat.Y); Assert.Equal(10, seat.ShowtimeId);
        var category = await f.Context.SeatCategories.SingleAsync(); Assert.Equal(10, category.ShowtimeId);
    }

    [Fact]
    public async Task GeometryModel_HasNullableProperties_NoDefaults_NoGeneratedValues()
    {
        await using var f = await LocalPostgresDatabase.CreateAsync();
        var seat = f.Context.Model.FindEntityType(typeof(Seat))!;
        foreach (var name in new[] { nameof(Seat.X), nameof(Seat.Y) })
        {
            var p = seat.FindProperty(name)!; Assert.True(p.IsNullable);
            Assert.Null(p.FindAnnotation(RelationalAnnotationNames.DefaultValue)); Assert.Equal(ValueGenerated.Never, p.ValueGenerated);
        }
    }

    private static async Task<string> Snapshot(AppDbContext db)
    {
        var rows = await db.Database.SqlQueryRaw<string>("""
            SELECT 'seats:'||md5(string_agg(to_jsonb(s)::text,',' ORDER BY id)) AS "Value" FROM seats s
            UNION ALL SELECT 'categories:'||md5(string_agg(to_jsonb(c)::text,',' ORDER BY id)) FROM seat_categories c
            UNION ALL SELECT 'seatSeq:'||last_value||':'||is_called FROM seats_id_seq
            UNION ALL SELECT 'categorySeq:'||last_value||':'||is_called FROM seat_categories_id_seq
            """).ToListAsync();
        return string.Join("|", rows.Order());
    }
}

public sealed class LegacyCoordinateAdapterTests
{
    [Fact]
    public void MissingGeometry_RemainsUnknown_ExplicitZeroRemainsZero()
    {
        var unknown = SeatMapDocumentAdapter.Convert(new SeatingChartFileDto { Seats = [new SeatDto { Row = "A", SeatNumber = "1" }] });
        Assert.Null(unknown.Seats![0]!.X); Assert.Null(unknown.Seats[0]!.Y);
        var zero = SeatMapDocumentAdapter.Convert(new SeatingChartFileDto { Seats = [new SeatDto { Row = "A", SeatNumber = "1", X = 0, Y = 0 }] });
        Assert.Equal(0, zero.Seats![0]!.X); Assert.Equal(0, zero.Seats[0]!.Y);
    }
    [Fact]
    public void HalfCoordinatePair_IsRejected()
    {
        Assert.Throws<SeatMapValidationException>(() => SeatMapDocumentAdapter.Convert(new SeatingChartFileDto { Seats = [new SeatDto { Row = "A", SeatNumber = "1", X = 3 }] }));
    }
}
