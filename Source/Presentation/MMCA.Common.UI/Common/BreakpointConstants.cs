using MudBlazor;

namespace MMCA.Common.UI.Common;

/// <summary>
/// Canonical breakpoint helpers for the MMCA design system.
/// Aligns C# viewport detection with CSS media query breakpoints.
/// </summary>
public static class BreakpointConstants
{
    /// <summary>
    /// Returns <see langword="true"/> when the viewport is below the sidebar-collapse threshold
    /// (MudBlazor Xs or Sm, i.e. &lt; 960 px). Used to switch between desktop data grids
    /// and mobile card-based layouts.
    /// </summary>
    public static bool IsMobileBreakpoint(Breakpoint breakpoint) =>
        breakpoint is Breakpoint.Xs or Breakpoint.Sm;
}
