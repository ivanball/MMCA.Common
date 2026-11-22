using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Pages.Auth.TwoFactor;

namespace MMCA.Common.UI.TwoFactor;

/// <summary>
/// Decides whether the router should answer the two-factor account page with the not-found page
/// because the host never registered the two-factor UI (no <c>AddTwoFactorUI()</c>, so no
/// <see cref="TwoFactorUIModule"/>): the page needs a client such a host does not have, and an API
/// that has no two-factor endpoints.
/// </summary>
internal static class TwoFactorPageGate
{
    /// <summary>Whether <paramref name="pageType"/> is hidden for this host.</summary>
    /// <param name="pageType">The page the router matched.</param>
    /// <param name="modules">The UI modules the host registered.</param>
    /// <returns><see langword="true"/> to render the not-found page instead of the matched page.</returns>
    public static bool Hides(Type pageType, IEnumerable<IUIModule> modules) =>
        pageType == typeof(TwoFactorAccount) && HidesTwoFactorPages(modules);

    /// <summary>Whether this host hides the two-factor page.</summary>
    /// <param name="modules">The UI modules the host registered.</param>
    /// <returns><see langword="true"/> when the page answers with the not-found page.</returns>
    public static bool HidesTwoFactorPages(IEnumerable<IUIModule> modules) =>
        !modules.Any(m => m is TwoFactorUIModule);
}
