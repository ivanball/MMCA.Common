using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Folder-width rule (rubric §5), driven by the shared <see cref="FolderWidthTestsBase"/>: no folder under
/// this repo's <c>Source/</c> or <c>Tests/</c> tree holds more than the allowed number of direct code
/// files, so the layout stays feature by folder rather than drifting into technical buckets.
/// <para>
/// The exemptions are the framework's deliberately horizontal public namespaces: the application
/// contracts (<c>Application/Interfaces*</c>), the CQRS primitives (<c>Application/UseCases*</c>), the
/// shared auth contracts, the API startup extensions and the integration-test package root. Each is a
/// flat namespace that every consumer imports, so splitting it would rename public API for no locality
/// gain (the scorecard records this as the accepted §5 implementation cap). The decorator test folder
/// mirrors the exempt decorator source folder.
/// </para>
/// </summary>
public sealed class FolderWidthTests : FolderWidthTestsBase
{
    protected override string RepoRoot { get; } = ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx");

    /// <summary>
    /// One-concept folders kept flat on purpose: the decorator pipeline (nine cross-cutting concerns
    /// times command and query, so a split yields nine two-file folders), its test twin, and the
    /// entity marker interfaces. Every other formerly flat public namespace was split by concern in
    /// the second rubric §5 pass (see UPGRADING.md).
    /// </summary>
    protected override IReadOnlyCollection<string> ExemptFolderSuffixes =>
    [
        "MMCA.Common.Application/UseCases/Decorators",
        "MMCA.Common.Application.Tests/Decorators",
        "MMCA.Common.Domain/Interfaces",
    ];
}
