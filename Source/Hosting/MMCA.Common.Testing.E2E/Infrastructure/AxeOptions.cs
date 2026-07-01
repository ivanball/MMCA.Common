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

    /// <summary>
    /// <see cref="Wcag21Aa"/> with the <c>aria-input-field-name</c> rule disabled, for pages whose only
    /// violation is MudBlazor's internal <c>MudTablePager</c> "rows per page" select. MudBlazor 9.6.0
    /// (PR #13080, "MudSelect: Mirror combobox semantics onto the hidden-input presenter") added
    /// <c>role="combobox"</c> to the MudSelect presenter, but the pager's own select gets no accessible
    /// name and it is not reachable from app markup (no <c>Label</c>/<c>aria-label</c> parameter on
    /// <c>MudTablePager</c>). Accepted as an upstream limitation until MudBlazor labels the pager select;
    /// every other WCAG 2.1 AA rule still runs. Use ONLY on scans of a page whose sole combobox is a pager.
    /// </summary>
    public static AxeRunOptions Wcag21AaExceptMudPagerCombobox { get; } = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
        Rules = new Dictionary<string, RuleOptions>(StringComparer.Ordinal)
        {
            ["aria-input-field-name"] = new RuleOptions { Enabled = false },
        },
    };
}
