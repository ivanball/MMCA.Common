using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;

namespace MMCA.Common.API.Tests.Localization;

/// <summary>
/// Guards that the framework's <c>ErrorResources.resx</c> and <c>ErrorResources.es.resx</c> translate
/// every error code the framework itself emits (ADR-027), and carry no key for a code nothing emits.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the emitted codes are found: a documented source scan.</b> There is no catalog of code
/// constants to reflect over (codes are string literals handed to the <c>Error</c> factories), so this
/// test reads the C# sources of the server-side framework projects whose errors reach the API's error
/// localizer: <c>MMCA.Common.Shared</c>, <c>.Domain</c>, <c>.Application</c>, <c>.Infrastructure</c> and
/// <c>MMCA.Common.API</c>. It collects:
/// </para>
/// <list type="number">
/// <item>the code argument (the first positional argument, or the one named <c>code</c>) of every
/// <c>Error.Validation/Invariant/NotFoundError/Conflict/Unauthorized/Forbidden/UnprocessableEntity/
/// TooManyRequests/Failure/Unexpected(...)</c> call and every <c>new Error(...)</c>, plus the
/// unqualified factory calls that initialize the shared statics inside <c>Error.cs</c>;</item>
/// <item>every code-shaped argument (<c>Area.Name</c>, dot-separated PascalCase) of a call to a
/// pass-through helper that forwards its caller's code to a factory: <c>CommonInvariants.*</c>,
/// <c>UserOwnershipRule.CheckOwnership</c> and <c>.RequireUserId</c>.</item>
/// </list>
/// <para>
/// An argument resolves when it is a string literal or names a <c>const string</c> declared in the
/// scanned sources (followed through const-to-const aliases). A factory argument that does not resolve
/// must be one of the <see cref="PassThroughSites"/>, the helpers whose code is supplied by their
/// caller; any other unresolved site fails the scan, so a code the scan cannot read is never silently
/// left untranslated. <c>ProblemDetailsResultReader.cs</c> is excluded: it rebuilds errors on the
/// client from a response body, so its codes never pass through the API's localizer.
/// </para>
/// </remarks>
public sealed partial class ErrorResourceCoverageTests
{
    /// <summary>Factory sites whose code is a parameter supplied by the caller (consumer or framework).</summary>
    private static readonly HashSet<string> PassThroughSites = new(StringComparer.Ordinal)
    {
        "CommonInvariants.cs: code",
        "UserOwnershipRule.cs: code",
        "CurrentUserServiceExtensions.cs: code",
        "AuditableAggregateRootEntity.cs: notDeletedErrorCode",
        "ValidationFailureExtensions.cs: f.ErrorCode",
    };

    private static readonly string[] ScannedProjects =
    [
        Path.Combine("Source", "Core", "MMCA.Common.Shared"),
        Path.Combine("Source", "Core", "MMCA.Common.Domain"),
        Path.Combine("Source", "Core", "MMCA.Common.Application"),
        Path.Combine("Source", "Core", "MMCA.Common.Infrastructure"),
        Path.Combine("Source", "Presentation", "MMCA.Common.API"),
    ];

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.Ordinal)
    {
        "ProblemDetailsResultReader.cs",
    };

    private static readonly Lazy<Scan> EmittedCodes = new(ScanSources);

    [Fact]
    public void Scan_ResolvesEveryErrorConstruction_AndFindsTheFrameworkCatalog()
    {
        var scan = EmittedCodes.Value;

        scan.Unresolved.Should().BeEmpty(
            "every Error construction must pass a literal or a const code, or be a known pass-through helper; unresolved: {0}",
            string.Join("; ", scan.Unresolved));
        scan.Codes.Count.Should().BeGreaterThan(
            60,
            "a wrong scan root must not let the coverage gate pass having read nothing");
    }

    [Theory]
    [InlineData("ErrorResources.resx")]
    [InlineData("ErrorResources.es.resx")]
    public void EveryEmittedErrorCode_HasANonEmptyEntry(string resourceFile)
    {
        var entries = ReadResx(resourceFile);

        var missing = EmittedCodes.Value.Codes
            .Where(code => !entries.TryGetValue(code, out var value) || string.IsNullOrWhiteSpace(value))
            .Order(StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "{0} must translate every error code the framework emits; {1} missing: {2}",
            resourceFile,
            missing.Count,
            string.Join(", ", missing));
    }

    [Theory]
    [InlineData("ErrorResources.resx")]
    [InlineData("ErrorResources.es.resx")]
    public void EveryErrorResourceKey_IsAnEmittedCode(string resourceFile)
    {
        var orphans = ReadResx(resourceFile).Keys
            .Where(key => !EmittedCodes.Value.Codes.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        orphans.Should().BeEmpty(
            "{0} must not carry a key for a code nothing emits; orphaned: {1}",
            resourceFile,
            string.Join(", ", orphans));
    }

    private static Dictionary<string, string> ReadResx(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "Source", "Presentation", "MMCA.Common.API", "Resources", fileName);
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => (string?)data.Element("value") ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static Scan ScanSources()
    {
        var root = RepositoryRoot();
        var files = ScannedProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(Path.Combine(root, project), path)))
            .Where(path => !ExcludedFiles.Contains(Path.GetFileName(path)))
            .Select(path => (Name: Path.GetFileName(path), Text: StripComments(File.ReadAllText(path))))
            .ToList();

        var constants = CollectConstants(files.Select(file => file.Text));
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var (name, text) in files)
        {
            var factoryCalls = FactoryCall.Matches(text).AsEnumerable();
            if (string.Equals(name, "Error.cs", StringComparison.Ordinal))
            {
                factoryCalls = factoryCalls.Concat(StaticInitializerCall.Matches(text));
            }

            foreach (var call in factoryCalls)
            {
                var arguments = SplitArguments(text, call.Index + call.Length);
                var codeArgument = arguments.FirstOrDefault(argument => string.Equals(argument.Name, "code", StringComparison.Ordinal))
                    ?? arguments.FirstOrDefault();
                if (codeArgument is null)
                {
                    continue;
                }

                if (Resolve(codeArgument.Value, constants) is { } code)
                {
                    codes.Add(code);
                }
                else if (!PassThroughSites.Contains($"{name}: {codeArgument.Value}"))
                {
                    unresolved.Add($"{name}: {codeArgument.Value}");
                }
            }

            foreach (Match call in PassThroughHelperCall.Matches(text))
            {
                codes.UnionWith(SplitArguments(text, call.Index + call.Length)
                    .Select(argument => Resolve(argument.Value, constants))
                    .OfType<string>()
                    .Where(value => CodeShape.IsMatch(value)));
            }
        }

        return new Scan(codes, unresolved);
    }

    /// <summary>Maps each <c>const string</c> name to its value, following const-to-const aliases.</summary>
    private static Dictionary<string, string> CollectConstants(IEnumerable<string> sources)
    {
        var raw = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match match in sources.SelectMany(source => ConstDeclaration.Matches(source)))
        {
            var name = match.Groups["name"].Value;
            if (!raw.TryGetValue(name, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                raw[name] = values;
            }

            values.Add(match.Groups["value"].Value.Trim());
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in raw.Where(entry => entry.Value.Count == 1))
        {
            var expression = values.Single();
            for (var hops = 0; hops < 8 && !expression.StartsWith('"'); hops++)
            {
                var alias = expression[(expression.LastIndexOf('.') + 1)..];
                if (!raw.TryGetValue(alias, out var aliasValues) || aliasValues.Count != 1)
                {
                    break;
                }

                expression = aliasValues.Single();
            }

            if (StringLiteral.Match(expression) is { Success: true } literal)
            {
                resolved[name] = literal.Groups["text"].Value;
            }
        }

        return resolved;
    }

    /// <summary>A string literal resolves to its text; a (dotted) identifier resolves through the const map.</summary>
    private static string? Resolve(string argument, Dictionary<string, string> constants)
    {
        if (StringLiteral.Match(argument) is { Success: true } literal)
        {
            return literal.Groups["text"].Value;
        }

        if (!Identifier.IsMatch(argument))
        {
            return null;
        }

        return constants.TryGetValue(argument[(argument.LastIndexOf('.') + 1)..], out var value) ? value : null;
    }

    /// <summary>
    /// Splits the argument list that starts at <paramref name="start"/> (just after the opening
    /// parenthesis) into top-level arguments, skipping nested brackets, strings and characters.
    /// </summary>
    private static List<Argument> SplitArguments(string text, int start)
    {
        var arguments = new List<Argument>();
        var depth = 0;
        var current = start;
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            var following = i + 1;
            if (c is '"' or '\'')
            {
                following = SkipLiteral(text, i) + 1;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
            }
            else if (c is ',' or ')')
            {
                AddArgument(arguments, text[current..i]);
                current = i + 1;
                if (c == ')')
                {
                    break;
                }
            }

            i = following;
        }

        return arguments;
    }

    private static void AddArgument(List<Argument> arguments, string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return;
        }

        var named = NamedArgument.Match(value);
        arguments.Add(named.Success
            ? new Argument(named.Groups["name"].Value, named.Groups["value"].Value.Trim())
            : new Argument(null, value));
    }

    /// <summary>
    /// Returns the index of the closing quote of the literal opening at <paramref name="start"/>
    /// (regular, verbatim, raw or character literal).
    /// </summary>
    private static int SkipLiteral(string text, int start)
    {
        var quote = text[start];
        if (quote == '"' && string.CompareOrdinal(text, start, "\"\"\"", 0, 3) == 0)
        {
            var close = text.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 2;
        }

        var verbatim = quote == '"' && start > 0 && text[start - 1] == '@'
            || quote == '"' && start > 1 && text[start - 1] == '$' && text[start - 2] == '@';
        var i = start + 1;
        while (i < text.Length)
        {
            var escaped = !verbatim && text[i] == '\\'
                || verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"';
            if (!escaped && text[i] == quote)
            {
                return i;
            }

            i += escaped ? 2 : 1;
        }

        return text.Length;
    }

    /// <summary>Blanks out line and block comments (doc comments included), leaving literals intact.</summary>
    private static string StripComments(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c is '"' or '\'')
            {
                var end = Math.Min(SkipLiteral(text, i), text.Length - 1);
                builder.Append(text, i, end - i + 1);
                i = end + 1;
            }
            else if (c == '/' && next == '/')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == '/' && next == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                builder.Append(' ');
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        return builder.ToString();
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MMCA.Common.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("MMCA.Common.slnx not found above the test output directory.");
    }

    private static bool IsBuildOutput(string projectDirectory, string path)
    {
        var relative = Path.GetRelativePath(projectDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal)
            || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<![\w.])(?:Error\.(?:Validation|Invariant|NotFoundError|Conflict|Unauthorized|Forbidden|UnprocessableEntity|TooManyRequests|Failure|Unexpected)|new\s+Error)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FactoryCall { get; }

    [GeneratedRegex(@"(?<==\s*)(?:Validation|Invariant|NotFoundError|Conflict|Unauthorized|Forbidden|UnprocessableEntity|TooManyRequests|Failure|Unexpected)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StaticInitializerCall { get; }

    [GeneratedRegex(@"(?:(?<![\w.])CommonInvariants\.\w+|(?<![\w.])UserOwnershipRule\.CheckOwnership|\.RequireUserId)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PassThroughHelperCall { get; }

    [GeneratedRegex(@"\bconst\s+string\s+(?<name>\w+)\s*=\s*(?<value>[^;]+);", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConstDeclaration { get; }

    [GeneratedRegex(@"^""(?<text>(?:[^""\\]|\\.)*)""$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StringLiteral { get; }

    [GeneratedRegex(@"^[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Identifier { get; }

    [GeneratedRegex(@"^(?<name>[A-Za-z_]\w*)\s*:(?!:)\s*(?<value>.+)$", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedArgument { get; }

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeShape { get; }

    private sealed record Argument(string? Name, string Value);

    private sealed record Scan(HashSet<string> Codes, List<string> Unresolved);
}
