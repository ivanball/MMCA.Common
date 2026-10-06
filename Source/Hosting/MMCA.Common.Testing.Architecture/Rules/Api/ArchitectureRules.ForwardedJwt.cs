using System.Globalization;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string ForwardedJwtBearerCall = "AddForwardedJwtBearer(";
    private const string FailClosedAudience = "JwtAudience.RequireConfigured(";

    /// <summary>
    /// Every <c>.AddForwardedJwtBearer(...)</c> call under the given source roots resolves its audience
    /// fail-closed through <c>JwtAudience.RequireConfigured(...)</c> and carries no <c>??</c> fallback
    /// (ADR-004). A host that read <c>configuration["Jwt:Audience"] ?? "SomeApi"</c> would boot with an
    /// audience nobody configured and answer every request with a 401 that reads like a token problem.
    /// <para>
    /// The rule scans <c>.cs</c> TEXT, because the defect compiles perfectly. A call is an
    /// <c>AddForwardedJwtBearer(</c> preceded by a member-access dot (so the extension's own declaration
    /// and prose mentions are not calls); its argument list is read to the matching close parenthesis.
    /// Comment lines are blanked first. A missing root is a violation, and finding fewer calls than
    /// <paramref name="minimumCalls"/> fails, so a moved root cannot make the gate vacuous.
    /// </para>
    /// </summary>
    /// <param name="sourceRoots">Directories scanned recursively for <c>*.cs</c> files.</param>
    /// <param name="minimumCalls">The fewest calls the scan must find (the repo's service-host count).</param>
    public static void ForwardedJwtBearerAudienceIsFailClosed(IReadOnlyCollection<string> sourceRoots, int minimumCalls)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);

        var violations = new List<string>();
        var calls = 0;

        foreach (var root in sourceRoots)
        {
            if (!Directory.Exists(root))
            {
                violations.Add($"  - source root not found: {root}");
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(IsNotBuildOutput))
            {
                var text = BlankLineComments(File.ReadAllText(file));
                foreach (var (line, arguments) in ForwardedJwtBearerCalls(text))
                {
                    calls++;
                    if (!arguments.Contains(FailClosedAudience, StringComparison.Ordinal))
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - {file}:{line} passes an audience not resolved through JwtAudience.RequireConfigured"));
                    }
                    else if (arguments.Contains("??", StringComparison.Ordinal))
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - {file}:{line} carries a ?? fallback inside the AddForwardedJwtBearer call"));
                    }
                }
            }
        }

        if (calls < minimumCalls)
        {
            violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - found {calls} AddForwardedJwtBearer call(s), expected at least {minimumCalls}: the scan root moved or a host stopped validating forwarded tokens"));
        }

        ArchitectureAssert.NoViolations(violations,
            "Every AddForwardedJwtBearer call must pass audience: JwtAudience.RequireConfigured(configuration[JwtAudience.ConfigKey]) "
                + "with no ?? fallback, so a host with no Jwt:Audience fails at startup instead of validating against a hard-coded default (ADR-004)");
    }

    private static bool IsNotBuildOutput(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Blanks every line whose first non-blank characters are <c>//</c>, keeping line numbers.</summary>
    private static string BlankLineComments(string text) =>
        string.Join('\n', text.Split('\n').Select(static l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

    /// <summary>The 1-based line and argument text of each member-access <c>AddForwardedJwtBearer(</c> call.</summary>
    private static IEnumerable<(int Line, string Arguments)> ForwardedJwtBearerCalls(string text)
    {
        var index = 0;
        while (true)
        {
            var start = text.IndexOf(ForwardedJwtBearerCall, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            index = start + ForwardedJwtBearerCall.Length;

            var before = start - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before]))
            {
                before--;
            }

            if (before < 0 || text[before] != '.')
            {
                continue;
            }

            var end = MatchingCloseParenthesis(text, index);
            yield return (LineNumberAt(text, start), text[index..end]);
            index = end;
        }
    }

    /// <summary>The index of the parenthesis closing an argument list that opens just before <paramref name="from"/>.</summary>
    private static int MatchingCloseParenthesis(string text, int from)
    {
        var depth = 1;
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return text.Length;
    }
}
