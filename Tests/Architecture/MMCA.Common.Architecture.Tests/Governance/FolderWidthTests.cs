using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Folder-width rule (rubric §5), driven by the shared <see cref="FolderWidthTestsBase"/>: no folder under
/// this repo's <c>Source/</c> or <c>Tests/</c> tree holds more than the allowed number of direct code
/// files, so the layout stays feature by folder rather than drifting into technical buckets.
/// <para>
/// Three one-concept folders are exempt: the CQRS decorators (<c>Application/UseCases/Decorators</c>),
/// their test twin (<c>Application.Tests/Decorators</c>), and the entity marker interfaces
/// (<c>Domain/Interfaces</c>). Every other formerly flat public namespace was split by concern in the
/// second rubric §5 pass (see <see cref="ExemptFolderSuffixes"/> and UPGRADING.md).
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
