using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Opt-in fitness function: Application-layer code must not use the repository's raw
/// <c>IQueryable</c> surfaces — <c>Table</c>, <c>TableNoTracking</c>,
/// <c>TableNoTrackingSingleQuery</c>, <c>TableNoTrackingSplitQuery</c> (see
/// <c>IReadRepository&lt;TEntity, TIdentifierType&gt;</c>). A handler written against a raw
/// <c>IQueryable</c> is EF-coupled: its query shape cannot cross a gRPC boundary, so the module
/// loses the framework's monolith-to-microservice extraction promise. Handlers should use the
/// focused repository methods (readers, queriers, specifications) instead.
/// <para>
/// <b>Implementation and its limits:</b> NetArchTest and plain reflection cannot see member
/// <em>usage</em> inside method bodies, and this package deliberately carries no IL or Roslyn
/// dependency. The rule is therefore an honest textual scan: it reads the <c>.cs</c> files of the
/// map's module Application projects and flags lines matching <c>.Table</c> /
/// <c>.TableNoTracking*</c> member access. It cannot see through variable indirection
/// (<c>var q = repo.TableNoTracking;</c> is caught, but an interface alias re-exposing the
/// queryable is not), and it skips only whole-line <c>//</c> comments, so a match inside a string
/// literal or trailing comment is a (rare) false positive — record such files in
/// <see cref="AllowedFiles"/> with a justifying comment in the subclass.
/// </para>
/// <para>
/// Adoption in a repo with existing violations: subclass, run once, and move the reported file
/// names into <see cref="AllowedFiles"/> as a ratchet — new files stay clean while the allowlist
/// shrinks over time.
/// </para>
/// </summary>
public abstract partial class RawQueryableConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// File names (e.g. <c>"GetEventsHandler.cs"</c>) exempted from the rule — the adoption
    /// ratchet for repos with existing raw-queryable handlers. Empty by default.
    /// </summary>
    protected virtual IReadOnlyList<string> AllowedFiles => [];

    /// <summary>
    /// The Application source directories to scan. Defaults to each declared module's Application
    /// project directory (located by project name under the repo's <c>Source/</c> tree). Override
    /// to scan additional directories (e.g. a framework Application project) or a custom layout.
    /// </summary>
    protected virtual IEnumerable<string> ApplicationSourceDirectories()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        var sourceRoot = Path.Combine(repoRoot, "Source");

        foreach (var module in Map.ModuleNames)
        {
            var projectName = Map.RootNamespace(module, Layer.Application);
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, projectName, SearchOption.AllDirectories))
            {
                yield return directory;
            }
        }
    }

    [Fact]
    public void ApplicationLayer_DoesNotUseRawQueryableSurfaces()
    {
        var directories = ApplicationSourceDirectories().ToList();

        directories.Should().NotBeEmpty(
            because: "the raw-queryable scan found no Application source directories — the map declares no modules or the project folders moved; override ApplicationSourceDirectories() (a vacuous scan would verify nothing)");

        var offenders = new List<string>();
        foreach (var directory in directories)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    AllowedFiles.Contains(Path.GetFileName(file), StringComparer.Ordinal))
                {
                    continue;
                }

                offenders.AddRange(ScanFile(file));
            }
        }

        ArchitectureAssert.NoViolations(offenders,
            "Application-layer code must not use the raw IQueryable repository surfaces (Table/TableNoTracking/TableNoTrackingSingleQuery/TableNoTrackingSplitQuery) — raw-queryable handlers are EF-coupled and cannot move behind a gRPC boundary; use the focused repository methods, or record the file in AllowedFiles as a ratchet");
    }

    private static IEnumerable<string> ScanFile(string file)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                RawQueryableAccessRegex.IsMatch(lines[i]))
            {
                yield return string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"  - {Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
    }

    [GeneratedRegex(@"\.Table(NoTracking(SingleQuery|SplitQuery)?)?\b", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RawQueryableAccessRegex { get; }
}
