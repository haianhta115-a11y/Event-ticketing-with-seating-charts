using System.Text;
using EventTicketing.Api.Models.SeatingChart;
using EventTicketing.Api.Services;

namespace EventTicketing.Api.Endpoints;

public static class SeatingChartEndpoints
{
    public static IEndpointRouteBuilder MapSeatingChartEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/seating-charts")
                       .WithTags("Seating Charts");

        // 1. Endpoint kiểm tra tính hợp lệ của sơ đồ ghế (không lưu vào hệ thống)
        group.MapPost("/validate", async (
            HttpRequest request,
            ISeatingChartValidator validator,
            CancellationToken ct) =>
        {
            var result = await ProcessValidationAsync(request, validator, ct);
            return result.IsValid ? Results.Ok(result) : Results.BadRequest(result);
        })
        .WithName("ValidateSeatingChart")
        .WithSummary("Kiểm tra tính hợp lệ của sơ đồ ghế (không lưu vào hệ thống)")
        .Produces<SeatingChartValidationResult>(StatusCodes.Status200OK)
        .Produces<SeatingChartValidationResult>(StatusCodes.Status400BadRequest)
        .AllowAnonymous()
        .DisableAntiforgery();

        // 2. Endpoint tải lên sơ đồ ghế (từ chối toàn bộ nếu có bất kỳ lỗi nào)
        group.MapPost("/upload", async (
            HttpRequest request,
            ISeatingChartValidator validator,
            CancellationToken ct) =>
        {
            var result = await ProcessValidationAsync(request, validator, ct);

            // Quy tắc sống còn (s-06): Nếu có bất kỳ lỗi nào, từ chối toàn bộ file và không lưu trữ
            if (!result.IsValid)
            {
                return Results.BadRequest(result);
            }

            return Results.Created(
                "/api/seating-charts",
                new
                {
                    success = true,
                    message = "Tải lên và tiếp nhận sơ đồ ghế thành công.",
                    validation = result
                });
        })
        .WithName("UploadSeatingChart")
        .WithSummary("Tải lên sơ đồ ghế (từ chối toàn bộ nếu có bất kỳ lỗi nào)")
        .Produces(StatusCodes.Status201Created)
        .Produces<SeatingChartValidationResult>(StatusCodes.Status400BadRequest)
        .AllowAnonymous()
        .DisableAntiforgery();

        // 3. Endpoint cung cấp cấu trúc mẫu chuẩn cho tệp sơ đồ ghế
        group.MapGet("/template", () =>
        {
            var template = new SeatingChartFileDto
            {
                LayoutName = "Sơ đồ mẫu Nhà hát Lớn - Tầng 1",
                Venue = "Nhà hát Lớn Hà Nội",
                TotalCapacity = 4,
                Sections = new List<SectionDto>
                {
                    new()
                    {
                        Name = "VIP",
                        Category = "VIP",
                        DefaultPrice = 500000,
                        Rows = new List<RowDto>
                        {
                            new()
                            {
                                RowNumber = "A",
                                Seats = new List<SeatDto>
                                {
                                    new()
                                    {
                                        SeatNumber = "1",
                                        Price = 500000,
                                        Status = "Available",
                                        X = 1,
                                        Y = 1
                                    },
                                    new()
                                    {
                                        SeatNumber = "2",
                                        Price = 500000,
                                        Status = "Available",
                                        X = 2,
                                        Y = 1
                                    }
                                }
                            }
                        }
                    },
                    new()
                    {
                        Name = "Standard",
                        Category = "Standard",
                        DefaultPrice = 250000,
                        Rows = new List<RowDto>
                        {
                            new()
                            {
                                RowNumber = "B",
                                Seats = new List<SeatDto>
                                {
                                    new()
                                    {
                                        SeatNumber = "1",
                                        Price = 250000,
                                        Status = "Available",
                                        X = 1,
                                        Y = 2
                                    },
                                    new()
                                    {
                                        SeatNumber = "2",
                                        Price = 250000,
                                        Status = "Available",
                                        X = 2,
                                        Y = 2
                                    }
                                }
                            }
                        }
                    }
                }
            };

            return Results.Ok(template);
        })
        .WithName("GetSeatingChartTemplate")
        .WithSummary("Cung cấp cấu trúc mẫu chuẩn cho tệp sơ đồ ghế")
        .Produces<SeatingChartFileDto>(StatusCodes.Status200OK)
        .AllowAnonymous();

        return app;
    }

    private static async Task<SeatingChartValidationResult> ProcessValidationAsync(
        HttpRequest request,
        ISeatingChartValidator validator,
        CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

            if (file == null)
            {
                return SeatingChartValidationResult.SingleError(
                    "Tệp tải lên (file)",
                    "file",
                    "FILE_NOT_FOUND",
                    "Không tìm thấy tệp sơ đồ được đính kèm trong yêu cầu.");
            }

            if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return SeatingChartValidationResult.SingleError(
                    $"Tệp '{file.FileName}'",
                    "file.extension",
                    "INVALID_FILE_EXTENSION",
                    "Chỉ chấp nhận tệp sơ đồ có định dạng .json.");
            }

            if (file.Length == 0)
            {
                return SeatingChartValidationResult.SingleError(
                    $"Tệp '{file.FileName}'",
                    "file.length",
                    "EMPTY_FILE",
                    "Tệp sơ đồ rỗng (0 bytes).");
            }

            await using var stream = file.OpenReadStream();
            return await validator.ValidateJsonStreamAsync(stream, ct);
        }

        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(content))
        {
            return SeatingChartValidationResult.SingleError(
                "Nội dung yêu cầu (Body)",
                "root",
                "EMPTY_FILE",
                "Nội dung yêu cầu rỗng hoặc không chứa dữ liệu JSON.");
        }

        return validator.ValidateJsonString(content);
    }
}

