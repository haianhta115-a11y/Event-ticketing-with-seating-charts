using EventTicketing.Api.Models.SeatingChart;
using EventTicketing.Api.Services;
using Xunit;

namespace EventTicketing.Tests;

public class SeatingChartValidatorTests
{
    private readonly SeatingChartValidator _validator = new();

    [Fact]
    public void Validate_WhenJsonIsEmpty_RejectsEntireFile_WithEmptyFileError()
    {
        // Act
        var result = _validator.ValidateJsonString(string.Empty);

        // Assert
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("EMPTY_FILE", result.Errors[0].ErrorCode);
        Assert.Contains("rỗng", result.Errors[0].Message);
    }

    [Fact]
    public void Validate_WhenJsonSyntaxIsInvalid_RejectsEntireFile_WithSyntaxErrorAndLinePosition()
    {
        // Arrange: JSON bị lỗi cú pháp thiếu dấu ngoặc nhọn kết thúc
        var malformedJson = @"
        {
            ""layoutName"": ""Hội trường A"",
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": []
        ";

        // Act
        var result = _validator.ValidateJsonString(malformedJson);

        // Assert
        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Equal("INVALID_JSON_SYNTAX", result.Errors[0].ErrorCode);
        Assert.Contains("Dòng", result.Errors[0].Location);
        Assert.Contains("Cột", result.Errors[0].Location);
    }

    [Fact]
    public void Validate_WhenLayoutNameIsMissing_RejectsEntireFile_WithMissingLayoutNameError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""   "",
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 100000, ""status"": ""Available"" }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "MISSING_LAYOUT_NAME");
    }

    [Fact]
    public void Validate_WhenSectionsAndSeatsAreEmpty_RejectsEntireFile_WithEmptySeatingDataError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Sân vận động Mỹ Đình"",
            ""sections"": []
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "EMPTY_SEATING_DATA");
    }

    [Fact]
    public void Validate_WhenSectionNameIsMissing_RejectsEntireFile_WithMissingSectionNameError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Nhà hát Tuổi Trẻ"",
            ""sections"": [
                {
                    ""name"": """",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 100000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "MISSING_SECTION_NAME");
    }

    [Fact]
    public void Validate_WhenDuplicateSectionName_RejectsEntireFile_WithDuplicateSectionNameError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Trung tâm Hội nghị"",
            ""sections"": [
                {
                    ""name"": ""Khu VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [ { ""seatNumber"": ""1"", ""price"": 200000 } ]
                        }
                    ]
                },
                {
                    ""name"": ""Khu VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""B"",
                            ""seats"": [ { ""seatNumber"": ""1"", ""price"": 200000 } ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "DUPLICATE_SECTION_NAME");
    }

    [Fact]
    public void Validate_WhenRowNumberIsMissing_RejectsEntireFile_WithMissingRowNumberError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Nhà hát Lớn"",
            ""sections"": [
                {
                    ""name"": ""Khán đài A"",
                    ""rows"": [
                        {
                            ""rowNumber"": "" "",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 150000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "MISSING_ROW_NUMBER");
    }

    [Fact]
    public void Validate_WhenDuplicateRowInSameSection_RejectsEntireFile_WithDuplicateRowNumberError()
    {
        // Arrange: Trong cùng khu vực 'VIP' có hai hàng mang cùng mã 'A'
        var json = @"
        {
            ""layoutName"": ""Cung Thể Thao"",
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [ { ""seatNumber"": ""1"", ""price"": 500000 } ]
                        },
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [ { ""seatNumber"": ""2"", ""price"": 500000 } ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorCode == "DUPLICATE_ROW_NUMBER");
    }

    [Fact]
    public void Validate_WhenSeatNumberIsMissing_RejectsEntireFile_WithSpecificSeatLocation()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Sân khấu kịch"",
            ""sections"": [
                {
                    ""name"": ""Tầng 1"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""Row-1"",
                            ""seats"": [
                                { ""seatNumber"": """", ""price"": 120000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "MISSING_SEAT_NUMBER");
        Assert.Contains("Tầng 1", err.Location);
        Assert.Contains("Row-1", err.Location);
    }

    [Fact]
    public void Validate_WhenSeatHasNegativePrice_RejectsEntireFile_WithNegativePriceError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Rạp chiếu"",
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": -50000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "NEGATIVE_PRICE");
        Assert.Contains("VIP", err.Location);
        Assert.Contains("A", err.Location);
        Assert.Contains("1", err.Location);
        Assert.Contains("-50000", err.Message);
    }

    [Fact]
    public void Validate_WhenSeatHasInvalidStatus_RejectsEntireFile_WithInvalidStatusError()
    {
        // Arrange: Trạng thái 'SoldOut' không nằm trong danh sách trạng thái cơ bản hợp lệ
        var json = @"
        {
            ""layoutName"": ""Nhà thi đấu"",
            ""sections"": [
                {
                    ""name"": ""Khán đài B"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""1"",
                            ""seats"": [
                                { ""seatNumber"": ""10"", ""price"": 100000, ""status"": ""DaBanHetVe"" }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "INVALID_STATUS");
        Assert.Contains("DaBanHetVe", err.Message);
    }

    [Fact]
    public void Validate_WhenSeatHasNegativeCoordinates_RejectsEntireFile_WithInvalidCoordinatesError()
    {
        // Arrange
        var json = @"
        {
            ""layoutName"": ""Sân vận động"",
            ""sections"": [
                {
                    ""name"": ""Zone A"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""1"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 100000, ""x"": -3, ""y"": 4 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "INVALID_COORDINATES");
        Assert.Contains("X (-3)", err.Message);
    }

    [Fact]
    public void Validate_WhenDuplicateSeatInSameRow_RejectsEntireFile_WithDuplicateSeatError()
    {
        // Arrange: Ghế '5' xuất hiện 2 lần trong cùng một hàng 'B' của khu vực 'Standard'
        var json = @"
        {
            ""layoutName"": ""Hội trường lớn"",
            ""sections"": [
                {
                    ""name"": ""Standard"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""B"",
                            ""seats"": [
                                { ""seatNumber"": ""5"", ""price"": 200000, ""x"": 1, ""y"": 1 },
                                { ""seatNumber"": ""5"", ""price"": 200000, ""x"": 2, ""y"": 1 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "DUPLICATE_SEAT");
        Assert.Contains("Ghế '5'", err.Location);
        Assert.Contains("Standard", err.Location);
        Assert.Contains("B", err.Location);
        Assert.Contains("trùng lặp", err.Message);
    }

    [Fact]
    public void Validate_WhenSeatsShareSameCoordinates_RejectsEntireFile_WithDuplicateCoordinatesError()
    {
        // Arrange: Ghế '1' và Ghế '2' có cùng tọa độ (X: 10, Y: 20)
        var json = @"
        {
            ""layoutName"": ""Trung tâm nghệ thuật"",
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 300000, ""x"": 10, ""y"": 20 },
                                { ""seatNumber"": ""2"", ""price"": 300000, ""x"": 10, ""y"": 20 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "DUPLICATE_COORDINATES");
        Assert.Contains("X: 10, Y: 20", err.Location);
        Assert.Contains("Xung đột", err.Message);
    }

    [Fact]
    public void Validate_WhenTotalCapacityMismatchesSeatCount_RejectsEntireFile_WithCapacityMismatchError()
    {
        // Arrange: Khai báo 50 ghế nhưng dữ liệu chỉ chứa 2 ghế
        var json = @"
        {
            ""layoutName"": ""Nhà hát"",
            ""totalCapacity"": 50,
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 100000 },
                                { ""seatNumber"": ""2"", ""price"": 100000 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.ErrorCode == "CAPACITY_MISMATCH");
        Assert.Contains("50", err.Message);
        Assert.Contains("2 ghế", err.Message);
    }

    [Fact]
    public void Validate_WhenMultipleErrorsExistInDifferentPlaces_RejectsEntireFile_AndReturnsAllSpecificErrors()
    {
        // Arrange: Một tệp có 4 lỗi khác nhau ở nhiều khu vực và hàng
        var json = @"
        {
            ""layoutName"": ""Sân khấu đa năng"",
            ""sections"": [
                {
                    ""name"": ""Khu A"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""1"",
                            ""seats"": [
                                { ""seatNumber"": """", ""price"": 100000 },
                                { ""seatNumber"": ""2"", ""price"": -20000 }
                            ]
                        }
                    ]
                },
                {
                    ""name"": ""Khu B"",
                    ""rows"": [
                        {
                            ""rowNumber"": ""1"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""price"": 100000, ""status"": ""SaiTrangThai"" }
                            ]
                        }
                    ]
                },
                {
                    ""name"": ""Khu C"",
                    ""rows"": []
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert: Quy tắc s-06: Toàn bộ tệp bị từ chối và báo rõ từng lỗi
        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);

        Assert.Contains(result.Errors, e => e.ErrorCode == "MISSING_SEAT_NUMBER" && e.Location.Contains("Khu A"));
        Assert.Contains(result.Errors, e => e.ErrorCode == "NEGATIVE_PRICE" && e.Location.Contains("Khu A"));
        Assert.Contains(result.Errors, e => e.ErrorCode == "INVALID_STATUS" && e.Location.Contains("Khu B"));
        Assert.Contains(result.Errors, e => e.ErrorCode == "EMPTY_SECTION" && e.Location.Contains("Khu C"));
    }

    [Fact]
    public void Validate_WhenSeatingChartIsValid_AcceptsEntireFile_WithZeroErrors()
    {
        // Arrange: Sơ đồ hợp lệ hoàn toàn với 3 ghế ở 2 khu vực
        var json = @"
        {
            ""layoutName"": ""Nhà thi đấu Phú Thọ"",
            ""venue"": ""TP. Hồ Chí Minh"",
            ""totalCapacity"": 3,
            ""sections"": [
                {
                    ""name"": ""VIP"",
                    ""category"": ""VIP"",
                    ""defaultPrice"": 500000,
                    ""rows"": [
                        {
                            ""rowNumber"": ""A"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""status"": ""Available"", ""x"": 1, ""y"": 1 },
                                { ""seatNumber"": ""2"", ""status"": ""Available"", ""x"": 2, ""y"": 1 }
                            ]
                        }
                    ]
                },
                {
                    ""name"": ""Standard"",
                    ""category"": ""Standard"",
                    ""defaultPrice"": 200000,
                    ""rows"": [
                        {
                            ""rowNumber"": ""B"",
                            ""seats"": [
                                { ""seatNumber"": ""1"", ""status"": ""Available"", ""x"": 1, ""y"": 2 }
                            ]
                        }
                    ]
                }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.TotalSeats);
        Assert.Equal(2, result.TotalSections);
        Assert.Equal(2, result.TotalRows);
        Assert.Contains("hợp lệ", result.Message);
    }

    [Fact]
    public void Validate_WhenFlatSeatListFormatIsValid_AcceptsEntireFile()
    {
        // Arrange: Định dạng danh sách phẳng (flat seats)
        var json = @"
        {
            ""layoutName"": ""Sân bóng rổ"",
            ""venue"": ""Cầu Giấy"",
            ""seats"": [
                { ""section"": ""Khán đài"", ""row"": ""1"", ""seatNumber"": ""A1"", ""price"": 150000, ""x"": 1, ""y"": 1 },
                { ""section"": ""Khán đài"", ""row"": ""1"", ""seatNumber"": ""A2"", ""price"": 150000, ""x"": 2, ""y"": 1 }
            ]
        }";

        // Act
        var result = _validator.ValidateJsonString(json);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.TotalSeats);
        Assert.Equal(1, result.TotalSections);
        Assert.Equal(1, result.TotalRows);
    }
}

