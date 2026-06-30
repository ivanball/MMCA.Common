using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.UI;

/// <summary>
/// Minimal, dependency-free render-snapshot (golden-markup) regression helper for bUnit component tests
/// (rubric §28). It captures a component's rendered markup, normalizes the non-deterministic bits MudBlazor
/// injects per render (GUIDs in element ids / ARIA associations), and compares the result to a committed
/// baseline stored under a <c>Snapshots/</c> folder next to the calling test. The caller asserts on the
/// returned <see cref="MarkupSnapshotResult.IsMatch"/> (kept dependency-free so the shipped package pulls in
/// no assertion library), so an unintended structural/markup change in a shared primitive fails the build.
/// <para>
/// Unlike pixel screenshots this comparison is deterministic and OS-independent (pure normalized markup),
/// so it runs identically on every CI platform and needs no per-platform golden management. Set the
/// <c>UPDATE_SNAPSHOTS=1</c> environment variable to (re)write baselines after an intentional change, then
/// review and commit the updated <c>.html</c> files. A missing baseline is written and reported as a
/// non-match (review-and-commit discipline) so a regression can never slip through on an absent snapshot.
/// </para>
/// </summary>
public static partial class MarkupSnapshot
{
    /// <summary>
    /// Compares <paramref name="markup"/> to the committed baseline named <paramref name="snapshotName"/>
    /// (under <c>Snapshots/</c> next to the calling test file) and returns the result for the caller to
    /// assert on (e.g. <c>MarkupSnapshot.Match(cut.Markup, "X").IsMatch.Should().BeTrue(result.Message)</c>).
    /// </summary>
    /// <param name="markup">The rendered markup to snapshot (e.g. <c>cut.Markup</c>).</param>
    /// <param name="snapshotName">A stable, file-safe baseline name (one per component scenario).</param>
    /// <param name="callerFilePath">Supplied by the compiler; locates the <c>Snapshots/</c> folder.</param>
    public static MarkupSnapshotResult Match(string markup, string snapshotName, [CallerFilePath] string callerFilePath = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markup);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotName);

        var actual = Normalize(markup);
        var directory = Path.Combine(Path.GetDirectoryName(callerFilePath)!, "Snapshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, snapshotName + ".html");

        var updating = string.Equals(Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS"), "1", StringComparison.Ordinal);
        if (updating)
        {
            File.WriteAllText(path, actual);
            return new MarkupSnapshotResult(true, $"Snapshot '{snapshotName}' refreshed.");
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, actual);
            return new MarkupSnapshotResult(
                false,
                $"Snapshot '{snapshotName}' did not exist and was written to {path}. Review and commit it, then re-run.");
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? new MarkupSnapshotResult(true, $"Snapshot '{snapshotName}' matched.")
            : new MarkupSnapshotResult(false, BuildDiffMessage(snapshotName, path, expected, actual));
    }

    // Collapses per-render GUIDs (dashed and 32-char "N" form) to stable tokens and normalizes line
    // endings / trailing whitespace so the comparison only reacts to real markup changes.
    private static string Normalize(string markup)
    {
        var text = markup.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        text = GuidRegex.Replace(text, "{guid}");
        text = Hex32Regex.Replace(text, "{guid}");
        return string.Join('\n', text.Split('\n').Select(line => line.TrimEnd()));
    }

    private static string BuildDiffMessage(string snapshotName, string path, string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(expectedLines.Length, actualLines.Length);

        for (var i = 0; i < max; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "(missing)";
            var a = i < actualLines.Length ? actualLines[i] : "(missing)";
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                return $"Markup snapshot '{snapshotName}' did not match {path} at line {i + 1}.{Environment.NewLine}"
                    + $"  expected: {e}{Environment.NewLine}  actual:   {a}{Environment.NewLine}"
                    + "Set UPDATE_SNAPSHOTS=1 to refresh the baseline after an intentional change, then commit it.";
            }
        }

        return $"Markup snapshot '{snapshotName}' did not match {path} (length differs). "
            + "Set UPDATE_SNAPSHOTS=1 to refresh the baseline after an intentional change, then commit it.";
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRegex { get; }

    [GeneratedRegex("\\b[0-9a-fA-F]{32}\\b")]
    private static partial Regex Hex32Regex { get; }
}

/// <summary>The outcome of a <see cref="MarkupSnapshot.Match(string, string, string)"/> comparison.</summary>
/// <param name="IsMatch"><see langword="true"/> when the markup matched the committed baseline (or was refreshed).</param>
/// <param name="Message">A human-readable description (the first differing line on a mismatch).</param>
public readonly record struct MarkupSnapshotResult(bool IsMatch, string Message);
