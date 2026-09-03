namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// UI-architecture convention fitness function (rubric §18): the container/presentational split is held
/// by two mechanical caps, so it is enforced by CI rather than review. (1) Every <c>*.razor.cs</c>
/// code-behind under a repo's <c>Source/</c> must stay within <see cref="MaxCodeBehindLines"/> — a
/// ballooning code-behind is the canonical sign that page logic belongs in the injected UI service or an
/// extracted presentational sub-component. (2) Every <c>*.razor</c> file must keep its inline
/// <c>@code</c> block within <see cref="MaxInlineCodeLines"/> — substantial logic belongs in the
/// code-behind partial, keeping the <c>.razor</c> file presentational markup. Authored once here and
/// re-run as a thin subclass in each repo (the subclass supplies its <see cref="Map"/>; the caps are the
/// shared convention and should be overridden only with a recorded decision).
/// </summary>
public abstract class UIArchitectureConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The convention cap for a <c>*.razor.cs</c> code-behind file. 400 sits above every conforming page
    /// in the family repos while failing the known oversized dashboards the convention exists to prevent.
    /// </summary>
    protected virtual int MaxCodeBehindLines => 400;

    /// <summary>
    /// The cap for a <c>.razor</c> file's inline <c>@code</c> block, measured from the <c>@code</c> line
    /// to the end of the file (by convention the block is the file's tail). Small glue blocks are fine;
    /// anything larger belongs in a code-behind partial.
    /// </summary>
    protected virtual int MaxInlineCodeLines => 120;

    /// <summary>
    /// Minimum number of <c>*.razor.cs</c> files the scan must discover — a non-vacuous guard so a glob
    /// that matched nothing cannot let the gate pass without checking anything.
    /// </summary>
    protected virtual int MinimumCodeBehindFiles => 1;

    /// <summary>
    /// Repo-relative path fragments (ordinal match) excluded from the scan, e.g. a frozen archive
    /// project. Empty by default.
    /// </summary>
    protected virtual IReadOnlyList<string> ExcludedPathFragments => [];

    [Fact]
    public void CodeBehinds_StayWithinTheLineCap()
    {
        var codeBehinds = EnumerateSourceFiles("*.razor.cs").ToArray();

        (codeBehinds.Length >= MinimumCodeBehindFiles).Should().BeTrue(
            because: $"at least {MinimumCodeBehindFiles} code-behind file(s) under Source/ must be discovered, so the convention is actually verified");

        var offenders = codeBehinds
            .Select(f => new { File = f, Lines = File.ReadAllLines(f).Length })
            .Where(x => x.Lines > MaxCodeBehindLines)
            .Select(x => $"{Path.GetFileName(x.File)} ({x.Lines} lines)")
            .ToList();

        offenders.Should().BeEmpty(
            because: $"code-behind files must stay within {MaxCodeBehindLines} lines (the §18 container/presentational convention); move logic into the page's injected UI service or extract presentational sub-components. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void RazorFiles_KeepInlineCodeBlocksSmall()
    {
        var razorFiles = EnumerateSourceFiles("*.razor").ToArray();

        var offenders = new List<string>();
        foreach (var file in razorFiles)
        {
            var lines = File.ReadAllLines(file);
            var codeLine = Array.FindIndex(lines, static l => l.TrimStart().StartsWith("@code", StringComparison.Ordinal));
            if (codeLine < 0)
            {
                continue;
            }

            var blockLines = lines.Length - codeLine;
            if (blockLines > MaxInlineCodeLines)
            {
                offenders.Add($"{Path.GetFileName(file)} (~{blockLines} inline @code lines)");
            }
        }

        offenders.Should().BeEmpty(
            because: $"inline @code blocks must stay within {MaxInlineCodeLines} lines; substantial logic belongs in the code-behind partial so the .razor file stays presentational markup (§18). Offenders: "
            + string.Join(", ", offenders));
    }

    private IEnumerable<string> EnumerateSourceFiles(string pattern)
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        var sourceDir = Path.Combine(repoRoot, "Source");

        return Directory
            .EnumerateFiles(sourceDir, pattern, SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !ExcludedPathFragments.Any(fragment => p.Contains(fragment, StringComparison.Ordinal)));
    }
}
