using System.Runtime.CompilerServices;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const BindingFlags AllDeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// No dependency cycles between the top-level namespaces of a layer assembly. Types are grouped by
    /// the namespace segment directly beneath their <see cref="LayerRef.RootNamespace"/> (types sitting
    /// in the root namespace itself form their own node), a directed edge <c>A -&gt; B</c> is recorded
    /// whenever a type in <c>A</c> structurally references a type in <c>B</c> in the SAME assembly, and
    /// every strongly connected component of that graph is reported as a cycle path.
    /// <para>
    /// A namespace cycle is the classic "big ball of mud" early warning: it makes the two folders
    /// impossible to reason about, test or extract independently, and it is exactly the coupling that
    /// blocks the framework's monolith-to-microservice promise. Breaking one is usually a matter of
    /// moving the shared abstraction down (into the root namespace or a dedicated <c>Abstractions</c>
    /// namespace) rather than letting the two folders reach across at each other.
    /// </para>
    /// <para>
    /// <b>Edges considered:</b> base types, implemented interfaces, field types, property types, method
    /// return and parameter types (public AND non-public declared members) and attribute types, with
    /// generic arguments, array/by-ref/pointer element types recursively expanded.
    /// </para>
    /// <para>
    /// <b>Limitation - method-body blindness.</b> This package carries no IL or Roslyn dependency, so the
    /// rule is pure reflection over signatures. A reference that exists ONLY inside a method body (a
    /// local variable, a constructor call, a static call) is invisible to it, so the rule catches
    /// STRUCTURAL cycles (the ones that show up in the type surface) and cannot claim a clean report
    /// means zero coupling. Compiler-generated types (closure/iterator classes, which do leak some body
    /// references) are skipped deliberately so the result stays a signature-level statement rather than a
    /// half-body one. Types outside the layer's root namespace are ignored entirely.
    /// </para>
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedCycleNamespaces">
    /// Fully-qualified namespace nodes whose cycles are accepted by design. A cycle is skipped only when
    /// EVERY namespace on its path is listed, so an allowance can never hide a new cycle that merely
    /// touches an accepted namespace.
    /// </param>
    public static void NamespacesHaveNoDependencyCycles(
        IArchitectureMap map,
        IReadOnlyCollection<string>? allowedCycleNamespaces = null)
    {
        ArgumentNullException.ThrowIfNull(map);

        var allowed = new HashSet<string>(allowedCycleNamespaces ?? [], StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var layer in map.Layers)
        {
            foreach (var (component, path) in FindNamespaceCycles(BuildNamespaceGraph(layer)))
            {
                // The allowance must cover the WHOLE strongly connected component, not just the shortest
                // path rendered in the message: otherwise a new namespace joining an accepted tangle
                // would slip in without changing the reported path.
                if (component.TrueForAll(allowed.Contains))
                {
                    continue;
                }

                var extra = component.Where(n => !path.Contains(n, StringComparer.Ordinal)).ToList();
                var suffix = extra.Count == 0 ? string.Empty : $" (component also contains: {string.Join(", ", extra)})";
                violations.Add($"  - {layer.Assembly.GetName().Name}: {string.Join(" -> ", path)}{suffix}");
            }
        }

        ArchitectureAssert.NoViolations(
            violations,
            "the top-level namespaces of a layer assembly must form a directed acyclic graph - a cycle " +
            "means neither namespace can be understood, tested or extracted without the other; move the " +
            "shared abstraction down instead of letting the folders reach across at each other (accept a " +
            "by-design cycle explicitly via the allowed-cycle namespaces)");
    }

    /// <summary>
    /// Builds the namespace-to-namespace dependency graph of one layer assembly. Keys and values are
    /// fully-qualified namespace nodes; every referenced node is guaranteed to exist as a key.
    /// </summary>
    private static SortedDictionary<string, SortedSet<string>> BuildNamespaceGraph(LayerRef layer)
    {
        var graph = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var type in layer.Assembly.LoadableTypes)
        {
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                continue;
            }

            var from = NamespaceNode(type, layer.RootNamespace);
            if (from is null)
            {
                continue;
            }

            var targets = Edges(graph, from);
            foreach (var referenced in ReferencedTypes(type))
            {
                if (!ReferenceEquals(referenced.Assembly, layer.Assembly))
                {
                    continue;
                }

                var to = NamespaceNode(referenced, layer.RootNamespace);
                if (to is not null && !string.Equals(to, from, StringComparison.Ordinal))
                {
                    targets.Add(to);
                    _ = Edges(graph, to);
                }
            }
        }

        return graph;
    }

    private static SortedSet<string> Edges(SortedDictionary<string, SortedSet<string>> graph, string node)
    {
        if (graph.TryGetValue(node, out var existing))
        {
            return existing;
        }

        var created = new SortedSet<string>(StringComparer.Ordinal);
        graph[node] = created;
        return created;
    }

    /// <summary>
    /// The top-level namespace node a type belongs to: the root namespace itself, or the root plus the
    /// one segment directly beneath it. Null when the type sits outside the layer's root namespace.
    /// </summary>
    private static string? NamespaceNode(Type type, string rootNamespace)
    {
        var ns = type.Namespace;
        if (ns is null)
        {
            return null;
        }

        if (string.Equals(ns, rootNamespace, StringComparison.Ordinal))
        {
            return rootNamespace;
        }

        var prefix = rootNamespace + ".";
        if (!ns.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = ns[prefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        var segment = dot < 0 ? rest : rest[..dot];
        return string.Concat(prefix, segment);
    }

    /// <summary>Every type structurally referenced by a type's signature surface, generics expanded.</summary>
    private static HashSet<Type> ReferencedTypes(Type type)
    {
        var result = new HashSet<Type>();
        foreach (var candidate in SignatureTypes(type))
        {
            Expand(candidate, result);
        }

        return result;
    }

    private static IEnumerable<Type?> SignatureTypes(Type type)
    {
        yield return type.BaseType;

        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }

        foreach (var attribute in AttributeTypes(type))
        {
            yield return attribute;
        }

        foreach (var field in type.GetFields(AllDeclaredMembers))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(AllDeclaredMembers))
        {
            yield return property.PropertyType;

            foreach (var attribute in AttributeTypes(property))
            {
                yield return attribute;
            }
        }

        foreach (var method in type.GetMethods(AllDeclaredMembers))
        {
            yield return method.ReturnType;

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    /// <summary>The attribute types applied to a member, tolerating an attribute that cannot be loaded.</summary>
    private static IReadOnlyList<Type> AttributeTypes(MemberInfo member)
    {
        try
        {
            return [.. member.GetCustomAttributesData().Select(a => a.AttributeType)];
        }
        catch (Exception ex) when (ex is TypeLoadException or FileNotFoundException or FileLoadException)
        {
            return [];
        }
    }

    /// <summary>Adds a candidate and every type nested inside it (array element, generic argument) to the set.</summary>
    private static void Expand(Type? candidate, HashSet<Type> into)
    {
        while (candidate is not null && (candidate.IsArray || candidate.IsByRef || candidate.IsPointer))
        {
            candidate = candidate.GetElementType();
        }

        if (candidate is null || candidate.IsGenericParameter || !into.Add(candidate))
        {
            return;
        }

        if (candidate.IsGenericType)
        {
            foreach (var argument in candidate.GetGenericArguments())
            {
                Expand(argument, into);
            }
        }
    }

    /// <summary>
    /// Every cycle in the namespace graph, one per strongly connected component: the component's full
    /// member list plus the shortest path leading from its first node back to itself.
    /// </summary>
    private static List<(List<string> Component, List<string> Path)> FindNamespaceCycles(
        SortedDictionary<string, SortedSet<string>> graph)
    {
        var nodes = graph.Keys.ToList();
        var count = nodes.Count;
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            position[nodes[i]] = i;
        }

        var edge = new bool[count][];
        var reach = new bool[count][];
        for (var i = 0; i < count; i++)
        {
            edge[i] = new bool[count];
            reach[i] = new bool[count];
            foreach (var target in graph[nodes[i]])
            {
                edge[i][position[target]] = true;
                reach[i][position[target]] = true;
            }
        }

        Close(reach, count);

        var cycles = new List<(List<string> Component, List<string> Path)>();
        var claimed = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if (claimed[i])
            {
                continue;
            }

            claimed[i] = true;
            var component = new HashSet<int>();
            for (var j = i; j < count; j++)
            {
                if (reach[i][j] && reach[j][i])
                {
                    component.Add(j);
                    claimed[j] = true;
                }
            }

            if (component.Count > 1)
            {
                var members = component.Select(n => nodes[n]).Order(StringComparer.Ordinal).ToList();
                cycles.Add((members, ShortestCycle(i, component, edge, nodes)));
            }
        }

        return cycles;
    }

    /// <summary>Transitive closure of the reachability matrix (Floyd-Warshall over booleans).</summary>
    private static void Close(bool[][] reach, int count)
    {
        for (var k = 0; k < count; k++)
        {
            for (var i = 0; i < count; i++)
            {
                if (!reach[i][k])
                {
                    continue;
                }

                for (var j = 0; j < count; j++)
                {
                    reach[i][j] = reach[i][j] || reach[k][j];
                }
            }
        }
    }

    /// <summary>Breadth-first search for the shortest path from a node back to itself inside its component.</summary>
    private static List<string> ShortestCycle(int start, HashSet<int> component, bool[][] edge, List<string> nodes)
    {
        var previous = new Dictionary<int, int>();
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in component)
            {
                if (!edge[current][next])
                {
                    continue;
                }

                if (next == start)
                {
                    return TracePath(start, current, previous, nodes);
                }

                if (seen.Add(next))
                {
                    previous[next] = current;
                    queue.Enqueue(next);
                }
            }
        }

        // Unreachable for a component of size > 1, but a total function beats an exception here.
        return [.. component.Select(n => nodes[n])];
    }

    private static List<string> TracePath(int start, int last, Dictionary<int, int> previous, List<string> nodes)
    {
        var path = new List<string>();
        var at = last;
        while (at != start)
        {
            path.Add(nodes[at]);
            at = previous[at];
        }

        path.Add(nodes[start]);
        path.Reverse();
        path.Add(nodes[start]);
        return path;
    }
}
