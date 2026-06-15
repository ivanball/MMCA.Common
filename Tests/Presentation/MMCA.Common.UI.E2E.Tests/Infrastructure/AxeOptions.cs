using Deque.AxeCore.Commons;

namespace MMCA.Common.UI.E2E.Tests.Infrastructure;

/// <summary>
/// Axe run options for the gallery gate.
/// </summary>
internal static class AxeOptions
{
    /// <summary>
    /// Scopes the axe scan to the project's documented accessibility target — WCAG 2.1 AA (levels A
    /// and AA across WCAG 2.0 and 2.1). axe's "best-practice" rules are intentionally out of scope
    /// (deferred, like visual-snapshot regression) so the gate fails only on real WCAG 2.1 AA
    /// conformance violations in the shared UI, not on advisory best-practice findings.
    /// </summary>
    public static AxeRunOptions Wcag21Aa { get; } = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
    };
}
