using System.Text;
using EventTicketing.Api.SeatMaps;
using EventTicketing.Api.Services;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class SeatingChartPreparationTests
{
    private static Task<PreparedSeatingChart> Read(string json) =>
        new SeatingChartPreparation(new SeatingChartValidator()).ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task ValidNestedChart_InheritsAndNormalizesWithoutPrice_ReturnsNoErrors()
    {
        var result = await Read("""{"layoutName":"Test","sections":[{"name":"Main","category":" VIP ","rows":[{"rowNumber":" A ","seats":[{"seatNumber":" 1 ","x":2,"y":3},{"seatNumber":"2","x":3,"y":3}]}]}]}""");
        Assert.Empty(result.Validation.Errors); Assert.NotNull(result.Document);
        Assert.Equal(2, result.Validation.TotalSeats); Assert.Equal(1, result.Validation.TotalRows);
        Assert.Equal(1, result.Validation.TotalSections);
        var seat = result.Document.Seats![0]!;
        Assert.Equal("A", seat.Row); Assert.Equal("1", seat.Number); Assert.Equal("VIP", seat.Category);
        Assert.Equal(2, seat.X); Assert.Equal(3, seat.Y);
    }

    [Fact]
    public async Task MultipleTypeAndMissingFieldErrors_AreAllReportedInOnePass()
    {
        var result = await Read("""{"layoutName":42,"seats":[{"section":"S","row":false,"seatNumber":1,"x":"wrong","category":""},{"section":"S","row":"B","category":"VIP","isAccessible":"yes"}]}""");
        var errors = result.Validation.Errors;
        foreach (var path in new[] { "layoutName", "seats[0].row", "seats[0].seatNumber", "seats[0].x", "seats[1].isAccessible" })
            Assert.Contains(errors, e => e.Path == path && e.ErrorCode == "INVALID_FIELD_TYPE");
        Assert.Contains(errors, e => e.Path == "seats[0].category");
        Assert.Contains(errors, e => e.Path == "seats[1].seatNumber");
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task DuplicateIdentityAcrossSections_ReportsBothSeatPaths()
    {
        var result = await Read("""{"layoutName":"Test","seats":[{"section":"Left","row":"A","seatNumber":"1"},{"section":"Right","row":" a ","seatNumber":"1"}]}""");
        var error = Assert.Single(result.Validation.Errors, e => e.ErrorCode == "DUPLICATE_SEAT");
        Assert.Equal("seats[1].seatNumber", error.Path); Assert.Contains("seats[0]", error.Message);
        Assert.Null(result.Document);
    }

    [Theory]
    [InlineData("{}", "MISSING_LAYOUT_NAME")]
    [InlineData("null", "INVALID_ROOT_OBJECT")]
    [InlineData("[]", "INVALID_ROOT_OBJECT")]
    [InlineData("", "EMPTY_FILE")]
    [InlineData("{", "INVALID_JSON_SYNTAX")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[null]}", "NULL_SEAT")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\",\"category\":\" \"}]}", "REQUIRED_FIELD")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\",\"x\":1.5}]}", "INVALID_COORDINATES")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\",\"status\":\"Reserved\"}]}", "UNSUPPORTED_IMPORT_STATUS")]
    [InlineData("{\"layoutName\":\"X\",\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\",\"heldUntilUtc\":\"2030-01-01T00:00:00Z\"}]}", "FORBIDDEN_HOLD_DATA")]
    [InlineData("{\"layoutName\":\"X\",\"totalCapacity\":2,\"seats\":[{\"section\":\"S\",\"row\":\"A\",\"seatNumber\":\"1\"}]}", "CAPACITY_MISMATCH")]
    [InlineData("{\"layoutName\":\"X\",\"LayoutName\":\"Y\",\"seats\":[]}", "DUPLICATE_FIELD")]
    public async Task InvalidInput_HasStructuredErrorAndNoImportDocument(string json, string code)
    {
        var result = await Read(json);
        Assert.False(result.Validation.IsValid); Assert.Null(result.Document);
        Assert.Contains(result.Validation.Errors, e => e.ErrorCode == code && !string.IsNullOrEmpty(e.Path));
    }

    [Fact]
    public async Task AllNestedErrors_AreAggregatedIncludingParentMismatchAndLimits()
    {
        var json = $$"""{"layoutName":"X","sections":[{"name":"S","category":"","rows":[{"rowNumber":"A","seats":[{"row":"B","seatNumber":"{{new string('1', 51)}}","x":-1,"y":1.1},{"seatNumber":"2","status":"Held","heldUntilUtc":"2026-01-01T00:00:00Z"}]}]}]}""";
        var result = await Read(json);
        foreach (var code in new[] { "ROW_MISMATCH", "FIELD_TOO_LONG", "INVALID_COORDINATES", "REQUIRED_FIELD", "UNSUPPORTED_IMPORT_STATUS", "FORBIDDEN_HOLD_DATA" })
            Assert.Contains(result.Validation.Errors, e => e.ErrorCode == code);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task InvalidJson_ReturnsOneBasedPositionWithoutInternalExceptionText()
    {
        var result = await Read("{\n  \"layoutName\": \"X\",\n  \"seats\": [}\n}");
        var error = Assert.Single(result.Validation.Errors);
        Assert.Equal("INVALID_JSON_SYNTAX", error.ErrorCode);
        Assert.Contains("dòng 3", error.Message); Assert.Contains("cột", error.Message);
        Assert.DoesNotContain("System.", error.Message); Assert.DoesNotContain("BytePosition", error.Message);
    }

    [Fact]
    public async Task DuplicateCoordinates_AndInvalidStructure_AreReportedTogether()
    {
        var result = await Read("""{"layoutName":"X","sections":[{"name":"Empty","rows":[]}],"seats":[{"section":"S","row":"A","seatNumber":"1","x":1,"y":1},{"section":"S","row":"A","seatNumber":"2","x":1,"y":1}]}""");
        Assert.Contains(result.Validation.Errors, e => e.ErrorCode == "EMPTY_SECTION");
        Assert.Contains(result.Validation.Errors, e => e.ErrorCode == "DUPLICATE_COORDINATES" && e.Path == "seats[1].coordinates");
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task TooManySeats_IsRejectedBeforeProducingThousandsOfPerSeatErrors()
    {
        var result = await Read("{\"layoutName\":\"X\",\"seats\":[" + string.Join(',', Enumerable.Repeat("null", 20001)) + "]}");
        Assert.Equal("TOO_MANY_SEATS", Assert.Single(result.Validation.Errors).ErrorCode);
        Assert.Null(result.Document);
    }

    [Fact]
    public async Task FileAtExactly5Mb_IsAccepted_WhenJsonAndTrailingWhitespaceAreValid()
    {
        const string json = """{"layoutName":"X","seats":[{"section":"S","row":"A","seatNumber":"1"}]}""";
        var result = await Read(json.PadRight(SeatingChartPreparation.MaximumBytes));
        Assert.True(result.Validation.IsValid); Assert.Single(result.Document!.Seats!);
    }

    [Fact]
    public async Task UnicodeSyntaxError_ReportsCharacterColumnInsteadOfUtf8ByteOffset()
    {
        const string json = "{\"layoutName\":\"Đ\",\"seats\":[}";
        var result = await Read(json);
        var error = Assert.Single(result.Validation.Errors);
        Assert.Contains($"cột {json.IndexOf('}') + 1}.", error.Message);
    }

    [Fact]
    public async Task OversizedNonSeekableStream_IsBoundedBeforeParsing()
    {
        await using var stream = new NonSeekableStream(new byte[SeatingChartPreparation.MaximumBytes + 1]);
        var result = await new SeatingChartPreparation(new SeatingChartValidator()).ReadAsync(stream);
        Assert.Equal("FILE_TOO_LARGE", Assert.Single(result.Validation.Errors).ErrorCode);
        Assert.Null(result.Document);
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
