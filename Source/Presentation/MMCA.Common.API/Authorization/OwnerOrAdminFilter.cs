using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Action filter that ensures the authenticated user either has the configured bypass role or owns the
/// resource identified by the configured route parameter. The ownership check compares the route value
/// against the current user's configured owner claim. Returns 403 Forbidden if the user is neither
/// privileged nor the resource owner. The vocabulary (claim type, bypass role, route parameter) comes
/// from <see cref="OwnerOrAdminFilterOptions"/>, whose defaults preserve the original
/// <c>customer_id</c> / <c>Admin</c> / <c>id</c> behavior (ADR-033).
/// </summary>
/// <remarks>
/// Register as a scoped service and apply via <c>[ServiceFilter(typeof(OwnerOrAdminFilter))]</c>
/// on controllers that mix admin and owner access (e.g., shopping carts, orders, customers, bookmarks).
/// </remarks>
public sealed class OwnerOrAdminFilter(
    ICurrentUserService currentUserService,
    IOptions<OwnerOrAdminFilterOptions> options) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var settings = options.Value;

        if (OwnershipHelper.IsAdmin(currentUserService, settings.BypassRole))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var ownerId = currentUserService.GetClaimValue<int>(settings.OwnerClaimType);

        if (ownerId is null)
        {
            context.Result = new ForbidResult();
            return;
        }

        if (TryGetOwnerParameter(context, settings.OwnerParameterName, out var requestedId)
            && requestedId != ownerId.Value)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next().ConfigureAwait(false);
    }

    // The owner identifier can arrive as a route value (/customers/{id}) or as a model-bound
    // query/body argument (/bookmarks?userId=42); check the route first, then the bound arguments.
    private static bool TryGetOwnerParameter(ActionExecutingContext context, string parameterName, out int value)
    {
        if (context.RouteData.Values.TryGetValue(parameterName, out var routeValue)
            && routeValue is not null
            && int.TryParse(routeValue.ToString(), out value))
        {
            return true;
        }

        if (context.ActionArguments.TryGetValue(parameterName, out var argumentValue)
            && argumentValue is not null
            && int.TryParse(argumentValue.ToString(), out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
