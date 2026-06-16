using Deque.AxeCore.Commons;

namespace MMCA.Common.Testing.E2E.Infrastructure;

/// <summary>
/// Shared axe-core run options for E2E accessibility gates. Shipped in the package so every consumer
/// (the framework gallery and downstream apps) scans against the same documented target.
/// </summary>
public static class AxeOptions
{
    /// <summary>
    /// Scopes the axe scan to the documented accessibility target — WCAG 2.1 AA (levels A and AA across
    /// WCAG 2.0 and 2.1). axe's "best-practice" rules are intentionally out of scope (deferred, like
    /// visual-snapshot regression) so the gate fails only on real WCAG 2.1 AA conformance violations,
    /// not on advisory best-practice findings.
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
