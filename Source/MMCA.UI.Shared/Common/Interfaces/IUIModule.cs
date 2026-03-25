using System.Reflection;

namespace MMCA.UI.Shared.Common.Interfaces;

/// <summary>
/// Contract for a pluggable UI module. Each module provides navigation items and its assembly
/// reference so the Blazor host can discover Razor components at runtime. Modules may also
/// contribute components to the top app bar (e.g., cart icon) and the root layout (e.g., drawers).
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
}
