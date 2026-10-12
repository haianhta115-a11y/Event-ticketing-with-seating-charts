using EventTicketing.Api.Security;
using EventTicketing.Api.SeatMaps;
using Microsoft.AspNetCore.Http.Features;
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
            SeatingChartPreparation preparation,
            CancellationToken ct) =>
        {
            var result = await ProcessValidationAsync(request, preparation, ct);
            return result.IsValid ? Results.Ok(result) : Results.Json(result, statusCode: result.Errors.Any(e => e.ErrorCode == "FILE_TOO_LARGE") ? 413 : 400);
        })
        .WithName("ValidateSeatingChart")
        .WithSummary("Kiểm tra tính hợp lệ của sơ đồ ghế (không lưu vào hệ thống)")
        .Produces<SeatingChartValidationResult>(StatusCodes.Status200OK)
        .Produces<SeatingChartValidationResult>(StatusCodes.Status400BadRequest)
        .RequireAuthorization(AccessPolicies.EventManagement)
        .DisableAntiforgery();

        // 2. Endpoint tải lên sơ đồ ghế (từ chối toàn bộ nếu có bất kỳ lỗi nào)
        group.MapPost("/upload", async (
            HttpRequest request,
            SeatingChartPreparation preparation,
            CancellationToken ct) =>
        {
            var result = await ProcessValidationAsync(request, preparation, ct);

            // Quy tắc sống còn (s-06): Nếu có bất kỳ lỗi nào, từ chối toàn bộ file và không lưu trữ
            if (!result.IsValid)
            {
                return Results.Json(result, statusCode: result.Errors.Any(e => e.ErrorCode == "FILE_TOO_LARGE") ? 413 : 400);
            }

            return Results.Created(
                "/api/seating-charts",
                new
                {
                    success = true,
                    persisted = false,
                    message = "Tệp đã được kiểm tra, chưa lưu vào suất diễn. Dùng /api/showtimes/{showtimeId}/seats/import để lưu.",
                    validation = result
                });
        })
        .WithName("UploadSeatingChart")
        .WithSummary("Kiểm tra tệp theo contract legacy; không lưu sơ đồ vào suất diễn")
        .Produces(StatusCodes.Status201Created)
        .Produces<SeatingChartValidationResult>(StatusCodes.Status400BadRequest)
        .RequireAuthorization(AccessPolicies.EventManagement)
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
        .RequireAuthorization(AccessPolicies.EventManagement);

        return app;
    }

    private static async Task<SeatingChartValidationResult> ProcessValidationAsync(
        HttpRequest request,
        SeatingChartPreparation preparation,
        CancellationToken ct)
    {
        var limit = request.HasFormContentType ? 6 * 1024 * 1024 : SeatingChartPreparation.MaximumBytes;
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        if (request.ContentLength > limit) return SeatingChartPreparation.Failure("file", "FILE_TOO_LARGE", "Tệp sơ đồ tối đa 5 MB.").Validation;
        try
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = SeatingChartPreparation.MaximumBytes }, ct);
                return (await preparation.ReadFileAsync(form.Files.GetFile("file") ?? form.Files.FirstOrDefault(), ct)).Validation;
            }
            return (await preparation.ReadAsync(request.Body, ct)).Validation;
        }
        catch (BadHttpRequestException error) when (error.StatusCode == 413)
        { return SeatingChartPreparation.Failure("file", "FILE_TOO_LARGE", "Tệp sơ đồ tối đa 5 MB.").Validation; }
        catch (InvalidDataException error)
        {
            var tooLarge = error.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase);
            return SeatingChartPreparation.Failure("file", tooLarge ? "FILE_TOO_LARGE" : "INVALID_MULTIPART",
                tooLarge ? "Tệp sơ đồ tối đa 5 MB." : "Dữ liệu multipart không hợp lệ.").Validation;
        }
    }
}
