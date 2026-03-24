using System.Reflection;

namespace MMCA.UI.Shared.Common.Interfaces;

/// <summary>
/// Contract for a pluggable UI module. Each module provides navigation items and its assembly
/// reference so the Blazor host can discover Razor components at runtime.
/// </summary>
public interface IUIModule
{
    /// <summary>Navigation entries contributed by this module to the shared sidebar/menu.</summary>
    IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>Assembly containing Razor pages, used by <c>AddAdditionalAssemblies</c> for route discovery.</summary>
    Assembly Assembly { get; }
}
