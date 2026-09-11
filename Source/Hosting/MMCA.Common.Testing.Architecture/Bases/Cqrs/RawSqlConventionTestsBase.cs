using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Module code must reach SQL only through the parameterizing APIs: <c>FromSqlRaw</c>,
/// <c>SqlQueryRaw</c>, <c>ExecuteSqlRaw</c> and <c>ExecuteSqlRawAsync</c> are banned, while the
/// interpolated siblings (<c>FromSql</c>, <c>FromSqlInterpolated</c>, <c>SqlQuery</c>,
/// <c>ExecuteSql</c>, <c>ExecuteSqlInterpolated</c>) and the framework's
/// <c>IRawSqlQueryExecutor</c> stay allowed.
/// <para>
/// <b>Why the raw four are the dangerous ones.</b> Each takes a plain <see cref="string"/>, so a
/// value concatenated into the statement compiles and runs: the injection is a code-review question
/// rather than a compile error, and every distinct value produces its own statement text, which
/// defeats plan reuse on the server. The interpolated forms take a
/// <see cref="FormattableString"/> and turn every hole into a command parameter, so the same code
/// shape is safe by construction and the statement text stays stable. Passing a
/// <see cref="FormattableString"/> to a raw overload silently loses the parameters, which is exactly
/// the mistake this rule catches.
/// </para>
/// <para>
/// <b>Implementation and its limits:</b> the same honest textual scan as
/// <see cref="RawQueryableConventionTestsBase"/>, for the same reason (this package carries no IL or
/// Roslyn dependency, and reflection cannot see member usage inside method bodies). It reads the
/// <c>.cs</c> files under <see cref="ScannedSourceDirectories"/> and flags member access on the four
/// banned names, skipping whole-line <c>//</c> comments; a match inside a string literal or a
/// trailing comment is a (rare) false positive, recorded in <see cref="AllowedFiles"/> with a
/// justifying comment in the subclass.
/// </para>
/// </summary>
public abstract partial class RawSqlConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// File names (e.g. <c>"LegacyReportQuery.cs"</c>) exempted from the rule: the adoption ratchet
    /// for a repo with existing raw-SQL call sites. Empty by default.
    /// </summary>
    protected virtual IReadOnlyList<string> AllowedFiles => [];

    /// <summary>
    /// The source directories to scan. Defaults to every project directory the map attributes to a
    /// business module (located by project name under the repo's <c>Source/</c> tree), which is the
    /// whole of "module assemblies": a raw statement is as dangerous in a module's Infrastructure as
    /// in its Application. Override for a custom layout or to scan a framework project too.
    /// </summary>
    /// <returns>The directories whose <c>.cs</c> files are scanned.</returns>
    protected virtual IEnumerable<string> ScannedSourceDirectories()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        var sourceRoot = Path.Combine(repoRoot, "Source");

        foreach (var projectName in Map.Layers
            .Where(layer => !string.IsNullOrEmpty(layer.Module))
            .Select(layer => layer.RootNamespace)
            .Distinct(StringComparer.Ordinal))
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceRoot, projectName, SearchOption.AllDirectories))
            {
                yield return directory;
            }
        }
    }

    [Fact]
    public void ModuleCode_UsesParameterizedSqlOnly()
    {
        var directories = ScannedSourceDirectories().ToList();

        directories.Should().NotBeEmpty(
            because: "the raw-SQL scan found no source directories to read: the map declares no modules or the "
                + "project folders moved, and a vacuous scan would verify nothing. Override ScannedSourceDirectories()");

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
            "module code must reach SQL through the parameterizing overloads: FromSqlRaw, SqlQueryRaw, "
            + "ExecuteSqlRaw and ExecuteSqlRawAsync take a plain string, so an interpolated value is inlined "
            + "into the statement instead of being parameterized. Use FromSql/SqlQuery/ExecuteSql (or the "
            + "framework's IRawSqlQueryExecutor), whose FormattableString parameters cannot be concatenated");
    }

    /// <summary>Reports every non-comment line in one file that names a banned raw-SQL member.</summary>
    /// <param name="file">The source file to scan.</param>
    /// <returns>One formatted violation per matching line.</returns>
    private static IEnumerable<string> ScanFile(string file)
    {
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) &&
                RawSqlAccessRegex.IsMatch(lines[i]))
            {
                yield return string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"  - {Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
    }

    [GeneratedRegex(@"\.(FromSqlRaw|SqlQueryRaw|ExecuteSqlRaw|ExecuteSqlRawAsync)\b", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RawSqlAccessRegex { get; }
}
