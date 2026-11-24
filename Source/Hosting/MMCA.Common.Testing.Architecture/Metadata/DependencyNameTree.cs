using System.Buffers;
using System.Text;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>
/// The dependency names a rule looks for, held as a tree of name segments so a type is matched in one
/// walk. A name is a namespace or a type full name; it matches a type when its segments are a leading
/// run of the type's own segments: the namespace (for a nested type, the declaring type's full name)
/// followed by the type name. Segments are split on <c>.</c>, <c>:</c>, <c>/</c> and <c>+</c> and
/// compared whole and ordinally, so:
/// <list type="bullet">
/// <item><c>Azure</c> matches <c>Azure</c>, <c>Azure.Messaging.ServiceBus</c> and every type in them;</item>
/// <item><c>MMCA.Common.AI</c> never matches <c>MMCA.Common.API</c> (<c>AI</c> and <c>API</c> are different segments);</item>
/// <item>an entry ending in a dot (<c>Google.</c>) has an empty last segment and matches nothing;</item>
/// <item>a generic type matches through its arity marker (<c>System.Collections.Generic.List`1</c>), and
/// its type arguments are matched on their own, as separate dependencies.</item>
/// </list>
/// </summary>
internal sealed class DependencyNameTree
{
    private static readonly SearchValues<char> Separators = SearchValues.Create(".:/+");
    private static readonly SearchValues<char> Unsupported = SearchValues.Create(",[]*&\\");

    private readonly Node _root = new();
    private readonly Dictionary<(TypeSigKind Kind, string FullName), Node[]> _matches = [];

    /// <summary>Builds the tree for the given dependency names.</summary>
    /// <exception cref="ArgumentException">A name carries an assembly qualifier, a bracketed generic argument list or an array, pointer or reference suffix.</exception>
    internal DependencyNameTree(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.AsSpan().IndexOfAny(Unsupported) >= 0)
            {
                throw new ArgumentException(
                    $"'{name}' is not a namespace or type name: assembly qualifiers, generic argument lists and array, pointer or reference suffixes are not supported.",
                    nameof(names));
            }

            var node = _root;
            foreach (var segment in Segments(name))
            {
                node = node.GetOrAdd(segment);
            }

            if (!node.IsTerminated)
            {
                node.IsTerminated = true;
                TerminatedCount++;
            }
        }
    }

    /// <summary>Gets the number of distinct names in the tree.</summary>
    internal int TerminatedCount { get; }

    /// <summary>The names in the tree that <paramref name="type"/> matches, as opaque tree nodes.</summary>
    internal IReadOnlyList<object> Match(TypeSig type)
    {
        var key = (type.Kind, type.FullName);
        if (!_matches.TryGetValue(key, out var matches))
        {
            matches = [.. Walk(Tokens(type))];
            _matches[key] = matches;
        }

        return matches;
    }

    /// <summary>True when any name in the tree is a leading run of the segments of <paramref name="fullName"/>.</summary>
    internal bool MatchesAny(string fullName) => Walk([fullName]).Any();

    /// <summary>Splits a name into segments, an empty segment kept wherever two separators touch or the name ends in one.</summary>
    private static IEnumerable<string> Segments(string name)
    {
        var end = -1;
        while (end != name.Length)
        {
            var start = end + 1;
            var length = name.AsSpan(start).IndexOfAny(Separators);
            end = length < 0 ? name.Length : start + length;

            yield return name[start..end];
        }
    }

    /// <summary>
    /// The name pieces of a type in reading order: the element's pieces and a suffix for an array,
    /// pointer or reference; otherwise the namespace (or declaring type) and the name, then, for a
    /// generic instance, an opening marker and each argument's pieces. Only the leading pieces can
    /// match a supported name; the rest keep a walk from matching across a boundary it should not.
    /// Built eagerly: <see cref="Match"/> caches the walk per type, so the list is built once per
    /// distinct type, and the walk still stops at the first piece no name continues into.
    /// </summary>
    private static List<string> Tokens(TypeSig type)
    {
        var tokens = new List<string>();
        AddTokens(type, tokens);
        return tokens;
    }

    private static void AddTokens(TypeSig type, List<string> tokens)
    {
        if (type.Kind is TypeSigKind.Array or TypeSigKind.Pointer or TypeSigKind.ByReference)
        {
            AddTokens(type.Element!, tokens);
            tokens.Add(SuffixToken(type));
        }
        else
        {
            var @namespace = type.IsNested ? type.DeclaringType!.FullName : type.Namespace;
            if (@namespace.Length > 0)
            {
                tokens.Add(@namespace);
            }

            tokens.Add(type.Name);
        }

        if (type.Kind != TypeSigKind.GenericInstance || type.Arguments.IsEmpty)
        {
            return;
        }

        tokens.Add("<");
        foreach (var argument in type.Arguments)
        {
            AddTokens(argument, tokens);
            tokens.Add(",");
        }
    }

    private static string SuffixToken(TypeSig type)
    {
        if (type.Kind == TypeSigKind.ByReference)
        {
            return "&";
        }

        if (type.Kind == TypeSigKind.Pointer)
        {
            return "*";
        }

        return type.Dimensions.Length > 1 ? "[,]" : "[]";
    }

    private IEnumerable<Node> Walk(IEnumerable<string> tokens)
    {
        var node = _root;
        foreach (var token in tokens)
        {
            foreach (var segment in Segments(token))
            {
                if (!node.TryGet(segment, out node))
                {
                    yield break;
                }

                if (node.IsTerminated)
                {
                    yield return node;
                }
            }
        }
    }

    private sealed class Node
    {
        private readonly Dictionary<string, Node> _children = [with(StringComparer.Ordinal)];

        internal bool IsTerminated { get; set; }

        internal Node GetOrAdd(string segment)
        {
            segment = segment.Normalize(NormalizationForm.FormC);
            if (!_children.TryGetValue(segment, out var child))
            {
                child = new Node();
                _children.Add(segment, child);
            }

            return child;
        }

        internal bool TryGet(string segment, out Node child) =>
            _children.TryGetValue(segment.Normalize(NormalizationForm.FormC), out child!);
    }
}
