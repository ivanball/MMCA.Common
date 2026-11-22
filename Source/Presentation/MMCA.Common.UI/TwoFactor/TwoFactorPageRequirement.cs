using Microsoft.AspNetCore.Authorization;

namespace MMCA.Common.UI.TwoFactor;

/// <summary>
/// The requirement behind the policy the two-factor account page declares instead of a bare
/// <c>[Authorize]</c>: an authenticated caller, exactly as <c>[Authorize]</c> asks, except when the
/// host never registered the two-factor UI (no <c>AddTwoFactorUI()</c>). Then every caller passes, so
/// the request reaches the router, whose <see cref="TwoFactorPageGate"/> answers it with the not-found
/// page (404) rather than a sign-in challenge for a page that does not exist for this host. The
/// notification pages' policy follows the same rule. Evaluated by
/// <see cref="TwoFactorPageAuthorizationHandler"/>.
/// </summary>
internal sealed class TwoFactorPageRequirement : IAuthorizationRequirement
{
    /// <summary>The policy name the two-factor account page references.</summary>
    public const string PolicyName = "mmca:two-factor-pages";
}
