using System.Reflection;

namespace MMCA.Common.UI.Common.Interfaces;

/// <summary>
/// Contract for a pluggable UI module. Each module provides navigation items and its assembly
/// reference so the Blazor host can discover Razor components at runtime. Modules may also
/// contribute components to the top app bar (e.g., cart icon), the root layout (e.g., drawers) and
/// the top of the main content region (e.g., banners shown above the page).
/// </summary>
public interface IUIModule
{
    /// <summary>Navigation entries contributed by this module to the shared sidebar/menu.</summary>
    IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Assembly containing Razor pages, used by <c>AddAdditionalAssemblies</c> for route discovery.</summary>
    Assembly Assembly { get; }

    /// <summary>Component types rendered inside the top app bar (e.g., cart icon with badge).</summary>
    IReadOnlyList<Type> AppBarComponentTypes => [];

    /// <summary>Component types rendered at the root layout level (e.g., drawers, overlays).</summary>
    IReadOnlyList<Type> LayoutComponentTypes => [];

    /// <summary>
    /// Component types rendered at the top of the main content region, above the page body (beside
    /// the offline banner), e.g. a banner the user must see before the page itself.
    /// </summary>
    IReadOnlyList<Type> ContentHeaderComponentTypes => [];
}
