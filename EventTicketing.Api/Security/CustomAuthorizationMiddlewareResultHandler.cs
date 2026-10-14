using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace EventTicketing.Api.Security;

public class CustomAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();
    private readonly ILogger<CustomAuthorizationMiddlewareResultHandler> _logger;

    public CustomAuthorizationMiddlewareResultHandler(ILogger<CustomAuthorizationMiddlewareResultHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure != null)
        {
            _logger.LogWarning("Caller lacks required role/policy at endpoint {Endpoint}",
                context.GetEndpoint()?.DisplayName ?? "Undeclared");
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
