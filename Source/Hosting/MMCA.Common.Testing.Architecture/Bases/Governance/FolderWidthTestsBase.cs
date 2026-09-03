namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Folder-width fitness function (rubric §5): module by project, feature by folder, use case by leaf.
/// No folder under <c>Source/</c> or <c>Tests/</c> holds more than <see cref="MaxDirectFiles"/> direct
/// code files, so a folder keeps naming a feature instead of drifting into a technical bucket.
/// Authored once here and re-run as a thin subclass in each repo, which supplies its
/// <see cref="RepoRoot"/> and, when it has documented exceptions, its
/// <see cref="ExemptFolderSuffixes"/>. A <c>.razor</c> component and its code-behind count as one
/// unit; resource files and generated files do not count at all; build output and tool-owned trees
/// (<c>bin</c>, <c>obj</c>, <c>Migrations</c>, <c>Platforms</c>, <c>Resources</c>, <c>wwwroot</c>) are
/// skipped outright.
/// </summary>
public abstract class FolderWidthTestsBase
{
    /// <summary>
    /// The repository root to walk, normally
    /// <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.
    /// </summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The highest number of direct code files a single folder may hold.</summary>
    protected virtual int MaxDirectFiles => 12;

    /// <summary>
    /// Repo-relative folder path suffixes (forward slashes) exempt from the rule, for the layouts a
    /// repo documents as deliberately flat.
    /// </summary>
    protected virtual IReadOnlyCollection<string> ExemptFolderSuffixes => [];

    [Fact]
    public void Folders_stay_narrow() =>
        ArchitectureRules.FoldersStayNarrow(RepoRoot, MaxDirectFiles, ExemptFolderSuffixes);
}
