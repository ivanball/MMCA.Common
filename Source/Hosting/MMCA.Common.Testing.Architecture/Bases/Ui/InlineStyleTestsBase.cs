namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Design-system guard (rubric section 20), driven by
/// <see cref="ArchitectureRules.RazorMarkupCarriesNoInlineStyles"/>: no <c>.razor</c> file under the
/// repo's <c>Source/</c> carries an inline style (<c>style=</c>, <c>Style=</c>, <c>CellStyle=</c>,
/// <c>HeaderStyle=</c>, <c>*StyleFunc=</c>). Authored once here and re-run as a thin subclass in each
/// repo, which supplies <see cref="RepoRoot"/> and <see cref="MinimumRazorFiles"/>; the floor proves the
/// scan actually walked the UI tree. There is no allow-list by default: override
/// <see cref="AllowedInlineStyles"/> only for a reviewed exemption.
/// </summary>
public abstract class InlineStyleTestsBase
{
    /// <summary>The repo root, typically <c>ArchitectureMapBase.FindRepoRoot("MyRepo.slnx")</c>.</summary>
    protected abstract string RepoRoot { get; }

    /// <summary>The fewest <c>.razor</c> files the scan must reach, so a moved root cannot pass vacuously.</summary>
    protected abstract int MinimumRazorFiles { get; }

    /// <summary>The directories scanned. Defaults to <c>Source</c> under <see cref="RepoRoot"/>.</summary>
    protected virtual IReadOnlyCollection<string> MarkupRoots => [Path.Combine(RepoRoot, "Source")];

    /// <summary>
    /// Reviewed exemptions, each a violation prefix relative to its markup root
    /// (<c>Folder/File.razor</c> or <c>Folder/File.razor:12</c>). Empty by default.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedInlineStyles => [];

    [Fact]
    public void RazorMarkup_CarriesNoInlineStyles() =>
        ArchitectureRules.RazorMarkupCarriesNoInlineStyles(MarkupRoots, MinimumRazorFiles, AllowedInlineStyles);
}
