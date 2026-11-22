using Microsoft.AspNetCore.Authorization;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.TwoFactor;

/// <summary>
/// Evaluates <see cref="TwoFactorPageRequirement"/>: an authenticated caller passes, and every caller
/// passes while the host hides the two-factor page because it never registered the two-factor UI (the
/// router then answers 404).
/// </summary>
/// <param name="modules">The UI modules the host registered.</param>
internal sealed class TwoFactorPageAuthorizationHandler(IEnumerable<IUIModule> modules)
    : AuthorizationHandler<TwoFactorPageRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TwoFactorPageRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (TwoFactorPageGate.HidesTwoFactorPages(modules)
            || context.User.Identities.Any(identity => identity.IsAuthenticated))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
