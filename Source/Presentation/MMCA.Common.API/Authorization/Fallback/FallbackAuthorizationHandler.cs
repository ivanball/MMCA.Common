using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MMCA.Common.API.Authorization.Fallback;

/// <summary>
/// Evaluates <see cref="FallbackAuthorizationRequirement"/>: an authenticated caller passes, and so
/// does a request whose path sits under one of
/// <see cref="FallbackAuthorizationOptions.ExemptPathPrefixes"/>. Everything else fails, which is
/// the point: an endpoint that declared no authorization at all is not published to anonymous
/// callers by accident.
/// </summary>
/// <param name="options">The fallback settings, including the exempt path prefixes.</param>
public sealed class FallbackAuthorizationHandler(IOptions<FallbackAuthorizationOptions> options)
    : AuthorizationHandler<FallbackAuthorizationRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        FallbackAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.Identity?.IsAuthenticated == true || IsExemptPath(context.Resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    // Under endpoint routing the authorization resource IS the HttpContext, so no
    // IHttpContextAccessor is needed; a non-HTTP resource (a SignalR hub invocation) simply has no
    // exempt path and falls through to the authenticated-user rule.
    private bool IsExemptPath(object? resource)
    {
        if (resource is not HttpContext httpContext)
        {
            return false;
        }

        var path = httpContext.Request.Path;

        return options.Value.ExemptPathPrefixes
            .Where(prefix => !string.IsNullOrEmpty(prefix))
            .Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
