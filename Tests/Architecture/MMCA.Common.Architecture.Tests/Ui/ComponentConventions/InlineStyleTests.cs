using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui.ComponentConventions;

/// <summary>
/// The framework's own inline-style ban (rubric section 20), driven by the shared
/// <see cref="InlineStyleTestsBase"/>: no <c>.razor</c> file under MMCA.Common's <c>Source/</c> (the
/// UI, UI.Web and UI.Maui projects) carries an inline style. Both consumers render these pages and
/// components, so the framework holds itself to the rule it ships. Layout lives as semantic classes
/// in <c>MMCA.Common.UI/wwwroot/app.css</c> or a component's scoped <c>.razor.css</c>.
/// </summary>
public sealed class InlineStyleTests : InlineStyleTestsBase
{
    protected override string RepoRoot { get; } = ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx");

    // A floor, not an equality: 56 .razor files under Source/ when the gate landed (53 in UI, 2 in
    // UI.Web, 1 in UI.Maui). Fewer means the scan root moved.
    protected override int MinimumRazorFiles => 50;
}
