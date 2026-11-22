using System.Reflection;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Components.Auth;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.TwoFactor;

/// <summary>
/// Two-factor authentication module descriptor, registered by <c>AddTwoFactorUI()</c>. Contributes the
/// account page's navigation item and the layout listener that sends a session refused for want of a
/// second factor to that page. A host that never registers it has no page (the router answers 404), no
/// nav item and no listener.
/// </summary>
public sealed class TwoFactorUIModule : IUIModule
{
    /// <summary>The route of the two-factor account page.</summary>
    public const string AccountPath = "/profile/two-factor";

    /// <inheritdoc />
    public IReadOnlyList<NavItem> NavItems { get; } =
    [
        new("Nav.TwoFactor", AccountPath, Icons.Material.Filled.Security, typeof(SharedResource), Section: NavSection.User),
    ];

    /// <inheritdoc />
    public IReadOnlyList<Type> LayoutComponentTypes { get; } = [typeof(MultiFactorRequiredListener)];

    /// <inheritdoc />
    public Assembly Assembly => typeof(TwoFactorUIModule).Assembly;
}
