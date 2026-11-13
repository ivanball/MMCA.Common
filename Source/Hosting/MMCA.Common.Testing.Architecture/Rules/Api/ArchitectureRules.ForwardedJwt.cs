using System.Globalization;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string ForwardedJwtBearerCall = "AddForwardedJwtBearer(";
    private const string ComposedServiceHostCall = "AddMmcaServiceHost(";
    private const string FailClosedAudience = "JwtAudience.RequireConfigured(";
    private const string TokenIssuerSelection = "ServiceHostAuthentication.TokenIssuer";

    /// <summary>
    /// Every service host under the given source roots that validates forwarded tokens resolves its
    /// audience fail-closed (ADR-004). A host that read <c>configuration["Jwt:Audience"] ?? "SomeApi"</c>
    /// would boot with an audience nobody configured and answer every request with a 401 that reads like
    /// a token problem. Two shapes count as such a host:
    /// <list type="bullet">
    /// <item><description>A direct <c>.AddForwardedJwtBearer(...)</c> call, which must pass its audience
    /// through <c>JwtAudience.RequireConfigured(...)</c> and carry no <c>??</c> fallback.</description></item>
    /// <item><description>A composed <c>.AddMmcaServiceHost(...)</c> call (MMCA.Common.ServiceHost) that
    /// does not select <c>ServiceHostAuthentication.TokenIssuer</c>. The composed host makes the guarded
    /// <c>AddForwardedJwtBearer</c> call itself, so it is fail-closed by construction and is only counted.
    /// A composed call is the token issuer, and is not counted, when
    /// <c>ServiceHostAuthentication.TokenIssuer</c> appears in its argument list or anywhere else in its
    /// file (options built in a variable), so options the scan cannot follow lower the count rather
    /// than raise it.</description></item>
    /// </list>
    /// <para>
    /// The rule scans <c>.cs</c> TEXT, because the defect compiles perfectly. A call is the method name
    /// and its open parenthesis preceded by a member-access dot (so a declaration and prose mentions are
    /// not calls); its argument list is read to the matching close parenthesis, across lines. Comment
    /// lines are blanked first. A missing root is a violation, and finding fewer hosts of the two shapes
    /// together than <paramref name="minimumCalls"/> fails, so a moved root cannot make the gate vacuous.
    /// </para>
    /// </summary>
    /// <param name="sourceRoots">Directories scanned recursively for <c>*.cs</c> files.</param>
    /// <param name="minimumCalls">
    /// The fewest forwarded-JWT hosts the scan must find, direct and composed calls together (the repo's
    /// number of service hosts that are not the token issuer).
    /// </param>
    public static void ForwardedJwtBearerAudienceIsFailClosed(IReadOnlyCollection<string> sourceRoots, int minimumCalls)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);

        var violations = new List<string>();
        var directCalls = 0;
        var composedCalls = 0;

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
                foreach (var (line, arguments) in MemberAccessCalls(text, ForwardedJwtBearerCall))
                {
                    directCalls++;
                    if (!arguments.Contains(FailClosedAudience, StringComparison.Ordinal))
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - {file}:{line} passes an audience not resolved through JwtAudience.RequireConfigured"));
                    }
                    else if (arguments.Contains("??", StringComparison.Ordinal))
                    {
                        violations.Add(string.Create(CultureInfo.InvariantCulture, $"  - {file}:{line} carries a ?? fallback inside the AddForwardedJwtBearer call"));
                    }
                }

                composedCalls += ForwardedComposedHostCalls(text);
            }
        }

        var hosts = directCalls + composedCalls;
        if (hosts < minimumCalls)
        {
            violations.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  - found {hosts} forwarded-JWT host(s) ({directCalls} AddForwardedJwtBearer call(s), {composedCalls} AddMmcaServiceHost call(s) that are not the token issuer), expected at least {minimumCalls}: the scan root moved or a host stopped validating forwarded tokens"));
        }

        ArchitectureAssert.NoViolations(violations,
            "Every AddForwardedJwtBearer call must pass audience: JwtAudience.RequireConfigured(configuration[JwtAudience.ConfigKey]) "
                + "with no ?? fallback (a composed AddMmcaServiceHost host does so by construction), so a host with no Jwt:Audience "
                + "fails at startup instead of validating against a hard-coded default (ADR-004)");
    }

    private static bool IsNotBuildOutput(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>Blanks every line whose first non-blank characters are <c>//</c>, keeping line numbers.</summary>
    private static string BlankLineComments(string text) =>
        string.Join('\n', text.Split('\n').Select(static l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : l));

    /// <summary>
    /// The number of member-access <c>AddMmcaServiceHost(</c> calls in <paramref name="text"/> that are
    /// not the token issuer: none when the file selects <c>ServiceHostAuthentication.TokenIssuer</c>
    /// anywhere, otherwise every call.
    /// </summary>
    private static int ForwardedComposedHostCalls(string text) =>
        text.Contains(TokenIssuerSelection, StringComparison.Ordinal)
            ? 0
            : MemberAccessCalls(text, ComposedServiceHostCall).Count();

    /// <summary>
    /// The 1-based line and argument text of each member-access call of <paramref name="call"/> (the
    /// method name followed by its open parenthesis).
    /// </summary>
    private static IEnumerable<(int Line, string Arguments)> MemberAccessCalls(string text, string call)
    {
        var index = 0;
        while (true)
        {
            var start = text.IndexOf(call, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            index = start + call.Length;

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
