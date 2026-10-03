using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MMCA.Common.API.Authorization.Fallback;

/// <summary>
/// Evaluates <see cref="FallbackAuthorizationRequirement"/>: an authenticated caller passes, and so
/// does a request whose path sits under one of
/// <see cref="FallbackAuthorizationOptions.ExemptPathPrefixes"/>. Everything else fails, which is
/// the point: an endpoint that declared no authorization at all is not published to anonymous
/// callers by accident.
/// <para>
/// A request that matched NO endpoint and names no file in the web root also passes. There is
/// nothing behind it to protect, and challenging it turned an unknown URL into a redirect to the
/// sign-in page for a signed-out visitor, so the host's
/// <c>UseStatusCodePagesWithReExecute("/not-found")</c> never got its 404 to render the not-found
/// page. Every request that matched an endpoint is decided exactly as before, and so is an unmatched
/// request for a file in the web root, which <c>UseStaticFiles</c> after the authorization
/// middleware would still serve.
/// </para>
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

        if (context.User.Identity?.IsAuthenticated == true
            || IsExemptPath(context.Resource)
            || IsUnmatchedRequest(context.Resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    // Under endpoint routing the authorization resource IS the HttpContext, so no
    // IHttpContextAccessor is needed; a non-HTTP resource (a SignalR hub invocation) simply has no
    // exempt path and falls through to the authenticated-user rule.
    private static bool IsUnmatchedRequest(object? resource)
    {
        if (resource is not HttpContext httpContext || httpContext.GetEndpoint() is not null)
        {
            return false;
        }

        // No endpoint matched, but static-file middleware later in the pipeline may still answer
        // with a file from the web root. Such a request keeps the fallback gate it always had.
        var webRoot = httpContext.RequestServices?.GetService<IWebHostEnvironment>()?.WebRootFileProvider;
        if (webRoot is null)
        {
            return true;
        }

        var path = httpContext.Request.Path.HasValue ? httpContext.Request.Path.Value : "/";
        return !webRoot.GetFileInfo(path).Exists && !webRoot.GetDirectoryContents(path).Exists;
    }

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
