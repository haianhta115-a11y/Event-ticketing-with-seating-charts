using EventTicketing.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EventTicketing.Api.Endpoints;

public sealed record ActivateRequest(string Token);
public sealed record ResendActivationRequest(string Email);

public static class RegistrationEndpoints
{
    public static void MapRegistrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();
        group.MapPost("/register", async (RegisterRequest request, RegistrationService service, CancellationToken ct) =>
        {
            var errors = RegistrationService.Validate(request);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            try { await service.RegisterAsync(request, ct); }
            catch (RegistrationUnavailableException)
            {
                return Results.Problem(statusCode: 503, title: "Registration unavailable", detail: "Đăng ký tạm thời không khả dụng. Vui lòng thử lại sau.");
            }
            return Results.Ok(new { message = RegistrationService.GenericMessage });
        }).WithName("Register");
        group.MapPost("/activate", async (ActivateRequest request, RegistrationService service, CancellationToken ct) =>
            await service.ActivateAsync(request.Token, ct)
                ? Results.Ok(new { message = "Tài khoản đã được kích hoạt. Bạn có thể đăng nhập." })
                : Results.BadRequest(new { error = new { code = "ACTIVATION_INVALID", message = "Liên kết không hợp lệ, đã dùng hoặc hết hạn. Bạn có thể yêu cầu liên kết mới." } }))
            .WithName("ActivateAccount");
        group.MapPost("/resend-activation", async (ResendActivationRequest request, RegistrationService service, HttpContext context, CancellationToken ct) =>
        {
            if (!RegistrationService.ValidEmail(request.Email))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Email không hợp lệ."] });
            var remaining = await service.ResendAsync(request.Email, ct);
            if (remaining > 0)
            {
                context.Response.Headers.RetryAfter = remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { message = "Đã đạt giới hạn gửi lại. Vui lòng thử lại sau.", remainingSeconds = remaining }, statusCode: 429);
            }
            return Results.Ok(new { message = RegistrationService.GenericMessage });
        }).WithName("ResendActivation");
    }
}
