using System.Security.Claims;
using EventTicketing.Api.Entities;
using Microsoft.AspNetCore.Authorization;

namespace EventTicketing.Api.Security;

public static class AccessPolicies
{
    public const string EventManagement = "EventManagement";
    public const string SeatBooking = "SeatBooking";
    public const string EventOwner = "EventOwner";

    public static int? UserId(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true &&
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : null;
}

public sealed class EventOwnerRequirement : IAuthorizationRequirement;

public sealed class EventOwnerHandler(ILogger<EventOwnerHandler> logger)
    : AuthorizationHandler<EventOwnerRequirement, Event>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        EventOwnerRequirement requirement, Event resource)
    {
        if (AccessPolicies.UserId(context.User) is int id &&
            (context.User.IsInRole("Admin") ||
             (context.User.IsInRole("Organizer") && resource.OwnerUserId == id)))
            context.Succeed(requirement);
        else
            logger.LogWarning("Authorization denied for event {EventId}", resource.Id);
        return Task.CompletedTask;
    }
}
