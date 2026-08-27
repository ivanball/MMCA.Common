using Mono.Cecil;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// A read-only index over the compiled assemblies a rule scans, built once per rule run: every type
/// by full name, every type's ancestor names (base classes and interfaces, transitively), and the
/// reverse map from an ancestor name to the types inside the scan that implement or derive from it.
/// <para>
/// The reverse map is what lets a call-graph walk follow an interface call: IL records the STATIC
/// callee (<c>IPointsAwarder.AwardAsync</c>), so without it a walk stops at the abstraction and
/// never reaches the implementation that does the work.
/// </para>
/// <para>
/// Ancestors are recorded by NAME, not by resolved definition, so a contract declared in an assembly
/// the map does not register (typically a framework interface such as <c>IDomainEventHandler{T}</c>)
/// is still recognized; only the walk THROUGH such a type stops, because its metadata is absent.
/// </para>
/// </summary>
internal sealed class CallGraphIndex
{
    private readonly Dictionary<string, TypeDefinition> _types = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, HashSet<string>> _ancestors = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, List<TypeDefinition>> _implementors = [with(StringComparer.Ordinal)];

    internal CallGraphIndex(IEnumerable<ModuleDefinition> modules)
    {
        foreach (var type in modules.SelectMany(m => m.GetTypes()))
        {
            _types[type.FullName] = type;
        }

        foreach (var type in _types.Values)
        {
            var ancestors = ComputeAncestors(type);
            _ancestors[type.FullName] = ancestors;

            foreach (var ancestor in ancestors)
            {
                if (!_implementors.TryGetValue(ancestor, out var implementors))
                {
                    implementors = [];
                    _implementors[ancestor] = implementors;
                }

                implementors.Add(type);
            }
        }
    }

    /// <summary>Every type in the scanned assemblies, nested types included.</summary>
    internal IEnumerable<TypeDefinition> Types => _types.Values;

    /// <summary>The type's full name with any generic instantiation stripped (<c>IFoo`1&lt;Bar&gt;</c> to <c>IFoo`1</c>).</summary>
    internal static string ElementFullName(TypeReference reference) => reference.GetElementType().FullName;

    /// <summary>True when the type derives from or implements the named contract, at any depth.</summary>
    internal bool Implements(TypeDefinition type, string ancestorFullName) =>
        _ancestors.TryGetValue(type.FullName, out var ancestors)
        && ancestors.Contains(ancestorFullName);

    /// <summary>The scanned type a reference points at, or null when it lives outside the scan.</summary>
    internal TypeDefinition? Find(TypeReference? reference) =>
        reference is null ? null : _types.GetValueOrDefault(ElementFullName(reference));

    /// <summary>
    /// Every method body a call could land on: the static target, plus every implementation inside the
    /// scanned assemblies when the target is an interface or virtual method. Overload resolution is
    /// approximated by name and parameter count, which over-approximates (a walk may follow one extra
    /// same-shape overload) rather than under-approximates, so no real path is missed.
    /// </summary>
    internal IEnumerable<MethodDefinition> TargetsOf(MethodReference callee)
    {
        var declaringType = Find(callee.DeclaringType);
        if (declaringType is null)
        {
            return [];
        }

        var direct = MatchingMethods(declaringType, callee).ToList();
        if (!declaringType.IsInterface && !direct.Exists(m => m.IsVirtual || m.IsAbstract))
        {
            return direct;
        }

        var implementors = _implementors.GetValueOrDefault(declaringType.FullName) ?? [];
        return direct.Concat(implementors.SelectMany(t => MatchingMethods(t, callee)));
    }

    /// <summary>
    /// The candidate implementations of a callee on one type. Explicit interface implementations are
    /// named <c>Some.Ns.IFoo.Method</c> in metadata, hence the suffix match.
    /// </summary>
    private static IEnumerable<MethodDefinition> MatchingMethods(TypeDefinition type, MethodReference callee) =>
        type.Methods.Where(m =>
            m.HasBody
            && m.Parameters.Count == callee.Parameters.Count
            && (string.Equals(m.Name, callee.Name, StringComparison.Ordinal)
                || m.Name.EndsWith("." + callee.Name, StringComparison.Ordinal)));

    /// <summary>The base type and directly implemented interfaces of a type.</summary>
    private static IEnumerable<TypeReference> DirectAncestorReferences(TypeDefinition type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }

        foreach (var implementation in type.Interfaces)
        {
            yield return implementation.InterfaceType;
        }
    }

    /// <summary>Every ancestor name of a type, walking as far as the scanned metadata allows.</summary>
    private HashSet<string> ComputeAncestors(TypeDefinition type)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<TypeDefinition>();
        pending.Push(type);

        while (pending.Count > 0)
        {
            foreach (var reference in DirectAncestorReferences(pending.Pop()))
            {
                var name = ElementFullName(reference);
                if (!ancestors.Add(name))
                {
                    continue;
                }

                var definition = _types.GetValueOrDefault(name);
                if (definition is not null)
                {
                    pending.Push(definition);
                }
            }
        }

        return ancestors;
    }
}
