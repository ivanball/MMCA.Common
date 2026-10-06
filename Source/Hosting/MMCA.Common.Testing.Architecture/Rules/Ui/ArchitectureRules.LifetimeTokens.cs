using System.Globalization;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Component-lifetime rule: no file under the given root reads <c>_cts.Token</c> (or
    /// <c>_cts!.Token</c>) directly. Reading <c>Token</c> off a disposed
    /// <see cref="CancellationTokenSource"/> throws <see cref="ObjectDisposedException"/>, so a load or
    /// handler that resumed after the user navigated away crashed the circuit instead of stopping.
    /// Components read the token through MMCA.Common.UI's <c>_cts.LifetimeToken()</c> (or a
    /// <c>LifetimeToken</c> property), which returns an already-cancelled token once <c>Dispose</c>
    /// has cancelled the source; the one permitted direct read is inside such a property's own
    /// expression-bodied declaration.
    /// </summary>
    /// <remarks>
    /// Scans <c>.razor</c> and <c>.razor.cs</c> files, plus every other <c>.cs</c> file when
    /// <paramref name="includeAllCodeFiles"/> is set (component bases written as plain classes).
    /// Finding fewer than <paramref name="minimumCodeBehindFiles"/> <c>.razor.cs</c> files fails, and
    /// so does a missing root, so a moved root cannot make the gate vacuous. Files under
    /// <c>bin</c>/<c>obj</c> are excluded.
    /// </remarks>
    /// <param name="scanRoot">The directory scanned recursively.</param>
    /// <param name="minimumCodeBehindFiles">The fewest <c>.razor.cs</c> files the scan must reach.</param>
    /// <param name="includeAllCodeFiles">Whether plain <c>.cs</c> files are scanned too.</param>
    public static void ComponentsReadTheirTokenThroughLifetimeToken(string scanRoot, int minimumCodeBehindFiles, bool includeAllCodeFiles = false)
    {
        ArgumentNullException.ThrowIfNull(scanRoot);

        var violations = new List<string>();

        if (!Directory.Exists(scanRoot))
        {
            violations.Add($"  - scan root not found: {scanRoot}");
        }
        else
        {
            var files = Directory
                .EnumerateFiles(scanRoot, "*.*", SearchOption.AllDirectories)
                .Where(IsNotBuildOutput)
                .Where(p => p.EndsWith(".razor", StringComparison.Ordinal)
                    || p.EndsWith(".razor.cs", StringComparison.Ordinal)
                    || includeAllCodeFiles && p.EndsWith(".cs", StringComparison.Ordinal))
                .ToList();

            var codeBehind = files.Count(static p => p.EndsWith(".razor.cs", StringComparison.Ordinal));
            if (codeBehind < minimumCodeBehindFiles)
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - scanned {codeBehind} .razor.cs file(s), expected at least {minimumCodeBehindFiles}: the scan must actually reach the component code-behind"));
            }

            violations.AddRange(files
                .SelectMany(file => DirectTokenReads(
                    Path.GetRelativePath(scanRoot, file).Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllLines(file)))
                .Select(static v => "  - " + v));
        }

        ArchitectureAssert.NoViolations(violations,
            "Reading _cts.Token after Dispose throws ObjectDisposedException. Read the token as _cts.LifetimeToken() "
                + "(MMCA.Common.UI.Common.ComponentLifetimeExtensions), or through a property declared as "
                + "'private CancellationToken LifetimeToken => _cts.LifetimeToken();', instead of _cts.Token");
    }

    /// <summary>
    /// The direct <c>_cts.Token</c> reads in one file's lines, as <c>relativePath:line ...</c>, skipping
    /// the expression-bodied <c>LifetimeToken</c> declaration itself. Public so the detector can be
    /// pinned line by line in the rule's own tests.
    /// </summary>
    /// <param name="relativePath">The path reported for the file.</param>
    /// <param name="lines">The file's lines.</param>
    /// <returns>One entry per offending line.</returns>
    public static IEnumerable<string> DirectTokenReads(string relativePath, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return DirectTokenReadsIterator(relativePath, lines);
    }

    private static IEnumerable<string> DirectTokenReadsIterator(string relativePath, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (DirectTokenRead.IsMatch(lines[i]) && !LifetimeTokenDeclaration.IsMatch(lines[i]))
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{relativePath}:{i + 1} reads _cts.Token directly; use LifetimeToken");
            }
        }
    }

    // _cts.Token or _cts!.Token, where _cts is the whole identifier (not _otherCts or _ctsFoo).
    [GeneratedRegex(@"(?<![\w])_cts!?\.Token\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DirectTokenRead { get; }

    // The expression-bodied LifetimeToken property itself, the one place the raw read is allowed.
    [GeneratedRegex(@"\bCancellationToken\s+LifetimeToken\s*=>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LifetimeTokenDeclaration { get; }
}
