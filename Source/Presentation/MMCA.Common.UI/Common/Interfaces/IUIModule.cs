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

    /// <summary>
    /// Component types rendered inside the layout's footer (the page's one <c>contentinfo</c>
    /// landmark), after the configured footer text and legal links, e.g. an app's own footer links or
    /// brand block. The footer is rendered when any module contributes one, even with no footer text
    /// and no legal links configured. Empty (the default) leaves the footer exactly as before.
    /// </summary>
    /// <remarks>
    /// Use this rather than <see cref="LayoutComponentTypes"/> for visible footer content: layout
    /// components are siblings of the sidebar and main region in the desktop flex row, so visible
    /// content there becomes an extra column instead of a footer.
    /// </remarks>
    IReadOnlyList<Type> FooterComponentTypes => [];

    /// <summary>
    /// A component type rendered by the framework's not-found page in place of its default content
    /// (icon, title, message and home button), so an app can show its own 404. The page keeps its
    /// title and still renders the Access Denied view for a re-executed 403. When several modules
    /// supply one, the first registered wins. <see langword="null"/> (the default) keeps the
    /// framework's not-found content.
    /// </summary>
    Type? NotFoundContentType => null;
}
