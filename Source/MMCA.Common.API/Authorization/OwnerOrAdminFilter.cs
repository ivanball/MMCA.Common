using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Action filter that ensures the authenticated user either has the Admin role or
/// owns the resource identified by the <c>id</c> route parameter. The ownership check
/// compares the route <c>id</c> against the current user's <c>customer_id</c> claim.
/// Returns 403 Forbidden if the user is neither an admin nor the resource owner.
/// </summary>
/// <remarks>
/// Register as a scoped service and apply via <c>[ServiceFilter(typeof(OwnerOrAdminFilter))]</c>
/// on controllers that mix admin and customer access (e.g., shopping carts, orders, customers).
/// </remarks>
public sealed class OwnerOrAdminFilter(ICurrentUserService currentUserService) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (OwnershipHelper.IsAdmin(currentUserService))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var customerId = currentUserService.GetClaimValue<int>("customer_id");

        if (customerId is null)
        {
            context.Result = new ForbidResult();
            return;
        }

        if (context.RouteData.Values.TryGetValue("id", out var routeIdValue)
            && routeIdValue is not null
            && int.TryParse(routeIdValue.ToString(), out var routeId)
            && routeId != customerId.Value)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next().ConfigureAwait(false);
    }
}
