using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Asserts the framework's own UI ships no hard-coded user-visible literals (ADR-027 / rubric §27):
/// snackbar messages, page titles, <c>&lt;PageTitle&gt;</c> markup, and breadcrumb labels in
/// <c>MMCA.Common.UI</c> must resolve through <c>IStringLocalizer</c> so the shared chrome follows the
/// selected language. Thin subclass of the shared <see cref="LocalizedTextConventionTestsBase"/> rule.
/// </summary>
public sealed class LocalizedTextConventionTests : LocalizedTextConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    // MMCA.Common.UI alone carries ~30 razor files; a floor of 20 catches a wrong scan root.
    protected override int MinimumScannedFiles => 20;
}
