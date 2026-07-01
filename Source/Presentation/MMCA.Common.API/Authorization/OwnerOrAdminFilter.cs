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

        if (context.RouteData.Values.TryGetValue(settings.RouteParameterName, out var routeIdValue)
            && routeIdValue is not null
            && int.TryParse(routeIdValue.ToString(), out var routeId)
            && routeId != ownerId.Value)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next().ConfigureAwait(false);
    }
}
