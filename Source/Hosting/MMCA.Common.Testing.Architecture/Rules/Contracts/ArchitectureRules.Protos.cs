using System.Text;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Frozen wire-contract guard for the SYNCHRONOUS cross-service API, the gRPC counterpart to
    /// <see cref="BuildIntegrationEventContract"/>. A <c>.proto</c> file is a published contract:
    /// a renumbered field, a retyped field, a renamed rpc or a dropped message silently breaks every
    /// peer built against the previous generation, and nothing in a single repo's build notices.
    /// </summary>
    /// <param name="protoRelativePaths">The <c>.proto</c> files to pin, relative to the repo root.</param>
    /// <param name="frozenContracts">The committed contract snapshot to compare against.</param>
    /// <param name="repoRootSolutionFileName">
    /// The solution file that marks the repo root (e.g. <c>MMCA.Store.slnx</c>), so the files are read
    /// from the working tree regardless of the test runner's working directory.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>What is pinned:</b> the file's <c>package</c>, every service with each of its rpcs (name,
    /// request type, response type, and both streaming flags), every message with each of its fields
    /// (name, declared type, label, and field NUMBER), and every enum with each of its values and
    /// numbers. Nested messages and enums are pinned under their qualified name.
    /// </para>
    /// <para>
    /// <b>What is deliberately not pinned:</b> <c>syntax</c>, <c>import</c> lines and <c>option</c>
    /// declarations (including <c>csharp_namespace</c>). None of them changes a byte on the wire, so
    /// including them would fail the gate on edits that break nobody, which is the fastest way to
    /// teach a team to update the snapshot without reading it.
    /// </para>
    /// <para>
    /// When a change is intentional, coordinate the peer rollout (or add a new field number rather
    /// than reusing one) and update the frozen list in the same commit.
    /// </para>
    /// </remarks>
    public static void ProtoContractsMatchFrozenList(
        IReadOnlyCollection<string> protoRelativePaths,
        IReadOnlyCollection<string> frozenContracts,
        string repoRootSolutionFileName)
    {
        ArgumentNullException.ThrowIfNull(protoRelativePaths);
        ArgumentNullException.ThrowIfNull(frozenContracts);

        var repoRoot = ArchitectureMapBase.FindRepoRoot(repoRootSolutionFileName);
        var absolutePaths = protoRelativePaths.Select(p => Path.Combine(repoRoot, p));

        AssertProtoContract(absolutePaths, frozenContracts);
    }

    /// <summary>
    /// The frozen-list comparison itself, over already-resolved absolute paths. Public so a repo can
    /// regenerate its snapshot (print the return of <see cref="BuildProtoContract"/>) and so the
    /// framework's own fitness self-tests can drive the rule from fixture files.
    /// </summary>
    /// <param name="absoluteProtoPaths">The <c>.proto</c> files to read.</param>
    /// <param name="frozenContracts">The committed contract snapshot to compare against.</param>
    public static void AssertProtoContract(
        IEnumerable<string> absoluteProtoPaths,
        IReadOnlyCollection<string> frozenContracts)
    {
        ArgumentNullException.ThrowIfNull(frozenContracts);

        var actual = BuildProtoContract(absoluteProtoPaths);
        var expected = frozenContracts.Order(StringComparer.Ordinal).ToList();

        var unexpected = actual.Except(expected, StringComparer.Ordinal)
            .Select(line => $"  + present in the .proto files but NOT frozen: {line}");
        var missing = expected.Except(actual, StringComparer.Ordinal)
            .Select(line => $"  - frozen but NOT present in the .proto files: {line}");

        ArchitectureAssert.NoViolations(
            unexpected.Concat(missing).Order(StringComparer.Ordinal),
            "the gRPC wire contract changed. These protos cross service boundaries, so a renumbered, "
            + "retyped, renamed or removed member breaks peers built against the previous generation. "
            + "If intentional, coordinate the peer rollout (or add a new field number instead of "
            + "reusing one) and update the frozen list in this commit");
    }

    /// <summary>
    /// Builds the contract snapshot: one deterministic, sorted line per service rpc, message field
    /// and enum value across the given <c>.proto</c> files.
    /// </summary>
    /// <param name="absoluteProtoPaths">The <c>.proto</c> files to read.</param>
    /// <returns>The sorted signature lines. A file that does not exist yields one explicit line saying so.</returns>
    public static List<string> BuildProtoContract(IEnumerable<string> absoluteProtoPaths)
    {
        ArgumentNullException.ThrowIfNull(absoluteProtoPaths);

        var lines = new List<string>();

        foreach (var path in absoluteProtoPaths)
        {
            if (!File.Exists(path))
            {
                lines.Add($"<missing proto file> {Path.GetFileName(path)}");
                continue;
            }

            lines.AddRange(DescribeProtoFile(File.ReadAllLines(path)));
        }

        return [.. lines.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Parses one proto3 file into its signature lines.</summary>
    /// <param name="fileLines">The file's raw lines.</param>
    /// <returns>The signature lines contributed by this file.</returns>
    /// <remarks>
    /// Parsed by STATEMENT, not by line: comments are stripped line by line, the remainder is joined
    /// and split at <c>{</c>, <c>}</c> and <c>;</c> outside quoted strings (see
    /// <see cref="SplitStatements"/>). A one-line body (<c>message Req { string id = 1; }</c>) then
    /// pins its members, and an rpc with an option body (<c>rpc A (B) returns (C) { option ...; }</c>)
    /// opens a scope of its own, so its closing brace no longer pops the enclosing service. One
    /// limitation remains: braces inside a FIELD option aggregate (<c>[(x) = {a: 1}]</c>) are not
    /// supported.
    /// </remarks>
    private static List<string> DescribeProtoFile(IReadOnlyList<string> fileLines)
    {
        var signatures = new List<string>();
        var scopes = new Stack<ProtoScope>();
        var package = string.Empty;
        var inBlockComment = false;

        var text = new StringBuilder();
        foreach (var rawLine in fileLines)
        {
            text.Append(StripComments(rawLine, ref inBlockComment)).Append(' ');
        }

        foreach (var statement in SplitStatements(text.ToString()))
        {
            if (statement == "}")
            {
                if (scopes.Count > 0)
                {
                    scopes.Pop();
                }

                continue;
            }

            var packageMatch = PackageLine.Match(statement);
            if (packageMatch.Success)
            {
                package = packageMatch.Groups["name"].Value;
                continue;
            }

            if (TryPushScope(statement, scopes))
            {
                continue;
            }

            if (DescribeStatement(statement, package, scopes) is { } signature)
            {
                signatures.Add(signature);
            }

            if (statement.EndsWith('{'))
            {
                // Any other block (an rpc's option body, an aggregate option value) opens an
                // anonymous scope, so its closing brace pops it rather than the enclosing scope.
                scopes.Push(new ProtoScope(ProtoScopeKind.Block, string.Empty));
            }
        }

        return signatures;
    }

    /// <summary>Renders one statement against the innermost open scope, or null outside every scope.</summary>
    /// <param name="statement">The trimmed statement.</param>
    /// <param name="package">The file's proto package.</param>
    /// <param name="scopes">The open scope stack (innermost first).</param>
    /// <returns>The signature line, or null.</returns>
    private static string? DescribeStatement(string statement, string package, Stack<ProtoScope> scopes) =>
        scopes.Count > 0 ? DescribeMember(statement, package, scopes, scopes.Peek().Kind) : null;

    /// <summary>
    /// Splits comment-free proto text into statements: a statement ends at <c>;</c> (kept), at
    /// <c>{</c> (kept, so a block header ends with it), and <c>}</c> is a statement of its own.
    /// Delimiters inside single- or double-quoted strings do not split. Blank statements are dropped.
    /// </summary>
    /// <param name="text">The file text with comments removed.</param>
    /// <returns>The trimmed statements in order.</returns>
    private static List<string> SplitStatements(string text)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaped = false;

        foreach (var c in text)
        {
            if (quote is not null)
            {
                current.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    current.Append(c);
                    break;
                case ';' or '{':
                    current.Append(c);
                    Flush(current, statements);
                    break;
                case '}':
                    Flush(current, statements);
                    statements.Add("}");
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush(current, statements);
        return statements;
    }

    /// <summary>Emits the accumulated statement when it is not blank, then resets the buffer.</summary>
    private static void Flush(StringBuilder current, List<string> statements)
    {
        var statement = current.ToString().Trim();
        if (statement.Length > 0)
        {
            statements.Add(statement);
        }

        current.Clear();
    }

    /// <summary>Pushes a service / message / enum / oneof scope when the line opens one.</summary>
    /// <param name="line">The comment-stripped, trimmed line.</param>
    /// <param name="scopes">The open scope stack.</param>
    /// <returns><see langword="true"/> when the line was a scope header.</returns>
    private static bool TryPushScope(string line, Stack<ProtoScope> scopes)
    {
        var header = ScopeHeader.Match(line);
        if (!header.Success)
        {
            return false;
        }

        var kind = header.Groups["kind"].Value switch
        {
            "service" => ProtoScopeKind.Service,
            "message" => ProtoScopeKind.Message,
            "enum" => ProtoScopeKind.Enum,
            _ => ProtoScopeKind.Oneof,
        };

        // A oneof is transparent: its members belong to the enclosing message, exactly as they do on
        // the wire, so it contributes no name segment.
        scopes.Push(new ProtoScope(kind, kind == ProtoScopeKind.Oneof ? string.Empty : header.Groups["name"].Value));
        return true;
    }

    /// <summary>Renders one rpc, field or enum value, or null when the line declares neither.</summary>
    /// <param name="line">The comment-stripped, trimmed line.</param>
    /// <param name="package">The file's proto package.</param>
    /// <param name="scopes">The open scope stack (innermost first).</param>
    /// <param name="kind">The innermost scope's kind.</param>
    /// <returns>The signature line, or null.</returns>
    private static string? DescribeMember(string line, string package, Stack<ProtoScope> scopes, ProtoScopeKind kind)
    {
        if (kind == ProtoScopeKind.Block)
        {
            // An rpc body or an aggregate option value: options only, nothing on the wire.
            return null;
        }

        var owner = QualifiedName(package, scopes);

        if (kind == ProtoScopeKind.Service)
        {
            var rpc = RpcLine.Match(line);
            if (!rpc.Success)
            {
                return null;
            }

            var request = $"{StreamPrefix(rpc.Groups["requestStream"].Value)}{rpc.Groups["request"].Value}";
            var response = $"{StreamPrefix(rpc.Groups["responseStream"].Value)}{rpc.Groups["response"].Value}";
            return $"service {owner}.{rpc.Groups["name"].Value}({request}) returns ({response})";
        }

        if (kind == ProtoScopeKind.Enum)
        {
            var value = EnumValueLine.Match(line);
            return value.Success
                ? $"enum {owner}.{value.Groups["name"].Value} = {value.Groups["number"].Value}"
                : null;
        }

        var field = FieldLine.Match(line);
        if (!field.Success)
        {
            return null;
        }

        var label = field.Groups["label"].Success ? $"{field.Groups["label"].Value} " : string.Empty;
        var type = NormalizeWhitespace.Replace(field.Groups["type"].Value, string.Empty);
        return $"message {owner}.{field.Groups["name"].Value} = {field.Groups["number"].Value} : {label}{type}";
    }

    /// <summary>The dotted owner name for the current scope, e.g. <c>mmca.catalog.Outer.Inner</c>.</summary>
    /// <param name="package">The file's proto package.</param>
    /// <param name="scopes">The open scope stack (innermost first).</param>
    /// <returns>The qualified owner name.</returns>
    private static string QualifiedName(string package, Stack<ProtoScope> scopes)
    {
        var segments = scopes.Reverse()
            .Select(s => s.Name)
            .Where(n => n.Length > 0);

        return string.Join('.', package.Length > 0 ? new[] { package }.Concat(segments) : segments);
    }

    /// <summary>Normalizes the optional <c>stream</c> keyword into a stable prefix.</summary>
    /// <param name="captured">The regex capture, empty when the rpc side is unary.</param>
    /// <returns><c>"stream "</c> or the empty string.</returns>
    private static string StreamPrefix(string captured) =>
        captured.Length > 0 ? "stream " : string.Empty;

    /// <summary>Removes line and block comments, returning the trimmed remainder.</summary>
    /// <param name="rawLine">The raw file line.</param>
    /// <param name="inBlockComment">Whether a block comment is currently open; updated in place.</param>
    /// <returns>The comment-free, trimmed line.</returns>
    private static string StripComments(string rawLine, ref bool inBlockComment)
    {
        // One left-to-right scan: a block comment that closes on the same line resumes the text after
        // it, and whichever of "//" and "/*" comes first decides what the rest of the line is.
        var kept = new StringBuilder();
        var rest = rawLine.AsSpan();

        while (!rest.IsEmpty)
        {
            if (inBlockComment)
            {
                var close = rest.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                inBlockComment = false;
                rest = rest[(close + 2)..];
                continue;
            }

            var lineComment = rest.IndexOf("//", StringComparison.Ordinal);
            var blockOpen = rest.IndexOf("/*", StringComparison.Ordinal);
            if (lineComment >= 0 && (blockOpen < 0 || lineComment < blockOpen))
            {
                kept.Append(rest[..lineComment]);
                break;
            }

            if (blockOpen < 0)
            {
                kept.Append(rest);
                break;
            }

            kept.Append(rest[..blockOpen]).Append(' ');
            inBlockComment = true;
            rest = rest[(blockOpen + 2)..];
        }

        return kept.ToString().Trim();
    }

    /// <summary>The kinds of proto block this parser tracks.</summary>
    private enum ProtoScopeKind
    {
        Service,
        Message,
        Enum,
        Oneof,

        /// <summary>Any other braced block (an rpc option body, an aggregate option value).</summary>
        Block,
    }

    /// <summary>One open proto block.</summary>
    /// <param name="Kind">What the block declares.</param>
    /// <param name="Name">The block's name, empty for a transparent <c>oneof</c>.</param>
    private sealed record ProtoScope(ProtoScopeKind Kind, string Name);

    [GeneratedRegex(@"^package\s+(?<name>[\w.]+)\s*;", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageLine { get; }

    [GeneratedRegex(
        @"^(?<kind>service|message|enum|oneof)\s+(?<name>\w+)\s*\{$",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ScopeHeader { get; }

    [GeneratedRegex(
        @"^rpc\s+(?<name>\w+)\s*\(\s*(?<requestStream>stream\s+)?(?<request>[\w.]+)\s*\)\s*returns\s*\(\s*(?<responseStream>stream\s+)?(?<response>[\w.]+)\s*\)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RpcLine { get; }

    [GeneratedRegex(
        @"^(?:(?<label>repeated|optional|required)\s+)?(?<type>(?:map\s*<[^>]+>)|[\w.]+)\s+(?<name>\w+)\s*=\s*(?<number>\d+)\s*[;\[]",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex FieldLine { get; }

    [GeneratedRegex(
        @"^(?<name>\w+)\s*=\s*(?<number>-?\d+)\s*[;\[]",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnumValueLine { get; }

    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NormalizeWhitespace { get; }
}
