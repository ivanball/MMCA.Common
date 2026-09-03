using Mono.Cecil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The framework's domain-event-handler interface, with Cecil's generic-arity marker.</summary>
    private const string DomainEventHandlerInterfaceFullName =
        "MMCA.Common.Application.Interfaces.Events.IDomainEventHandler`1";

    /// <summary>The member names that flush a unit of work to the database.</summary>
    private static readonly string[] SaveMemberNames = ["SaveChanges", "SaveChangesAsync"];

    /// <summary>
    /// The declaring-type suffixes that make a <c>SaveChanges</c>/<c>SaveChangesAsync</c> call a real
    /// persistence flush: EF's own contexts (<c>ApplicationDbContext</c>, <c>SQLServerDbContext</c>),
    /// the framework's <c>DbContextFactory</c> save surface, and the <c>IUnitOfWork</c> /
    /// <c>IRepository</c> abstractions that forward to them.
    /// </summary>
    private static readonly string[] SaveSurfaceTypeSuffixes =
        ["DbContext", "DbContextFactory", "UnitOfWork", "Repository"];

    /// <summary>
    /// Domain event handlers run INSIDE the save that raised the event (dispatch happens after
    /// <c>SaveChangesAsync</c>, or after commit inside a <c>ITransactional</c> command). A handler that
    /// saves again therefore opens a second write in the middle of the first one: it re-enters the
    /// change tracker, can raise a fresh event cascade, and persists work the outer transaction may
    /// still roll back. Handlers mutate state and let the owning unit of work flush it; anything that
    /// must be written independently belongs on the outbox.
    /// <para>
    /// This rule fails when a type implementing <c>IDomainEventHandler&lt;T&gt;</c> reaches a save
    /// TRANSITIVELY: through its own methods, or through the methods of any type it calls into inside
    /// the assemblies the map registers. A direct-dependency scan is not enough, because the real
    /// shape is a handler delegating to a service that saves.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> Neither NetArchTest nor reflection can see a CALL inside a method body, so
    /// the rule reads IL (through the Mono.Cecil already carried by NetArchTest) and walks the call
    /// graph breadth-first from every handler method. Callees are matched by full name, so the package
    /// keeps its deliberate zero-reference stance toward EF Core and the framework.
    /// </para>
    /// <para>
    /// <b>What the walk follows.</b> Direct calls, delegate creations (<c>ldftn</c>, so lambdas and
    /// local functions are covered), and interface/virtual calls, which are expanded to every
    /// implementation found inside the scanned assemblies. <c>async</c> and iterator methods are
    /// followed into their compiler-generated state machine, whose <c>MoveNext</c> holds the real body.
    /// </para>
    /// <para>
    /// <b>Allowlist entries</b> are type full names (<c>Some.Ns.PointsAwarder</c>) or namespaces
    /// (<c>Some.Ns.Points</c>), matched exactly as
    /// <see cref="HardDeletesOnlyInAllowedTypes"/> matches them. An entry does two things: it silences
    /// a handler it names, AND it stops the walk from descending into the type, which is the correct
    /// reading of "we accept what happens in there". That is why the default in
    /// <see cref="DomainEventHandlerSaveTestsBase"/> allowlists <c>MMCA.Common</c>: the framework's own
    /// outbox event bus saves by design, and a handler publishing an integration event is not the
    /// defect this rule hunts. A handler calling a save DIRECTLY is still reported, because detection
    /// happens at the call site and needs no descent.
    /// </para>
    /// <para>
    /// <b>Limits.</b> (1) The walk is bounded by <paramref name="maxCallDepth"/>; a save further away
    /// than that is not reported. (2) Interface dispatch is resolved by finding implementations in the
    /// scanned assemblies, so a handler whose collaborator is implemented in an assembly the map does
    /// not register is not followed; register the assembly, or keep the concrete type in scope.
    /// (3) Overloads are matched by name and parameter count, which can follow one extra same-shape
    /// overload; it over-reports rather than misses. (4) Reflection, DI-resolved delegates and
    /// expression trees are invisible to any static walk.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypesAndNamespaces">
    /// Type full names or namespace prefixes that are neither reported nor walked into.
    /// </param>
    /// <param name="maxCallDepth">How many calls deep the walk goes from a handler method. Minimum 1.</param>
    public static void DomainEventHandlersDoNotSave(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypesAndNamespaces,
        int maxCallDepth = 6)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypesAndNamespaces);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCallDepth, 1);

        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var location in ScannableAssemblyLocations(map))
            {
                modules.Add(ModuleDefinition.ReadModule(location));
            }

            var index = new CallGraphIndex(modules);

            var violations = index.Types
                .Where(type => index.Implements(type, DomainEventHandlerInterfaceFullName)
                    && !IsAllowed(type.FullName, allowedTypesAndNamespaces))
                .Select(type => (Handler: type, Path: FindSavePath(type, index, allowedTypesAndNamespaces, maxCallDepth)))
                .Where(found => found.Path is not null)
                .Select(found => $"  - {found.Handler.FullName}: {found.Path}")
                .Order(StringComparer.Ordinal)
                .ToList();

            ArchitectureAssert.NoViolations(violations,
                "a domain event handler runs inside the save that raised the event, so it must not "
                    + "reach SaveChanges/SaveChangesAsync itself or through anything it calls. Mutate "
                    + "state and let the owning unit of work flush it, or move the independent write "
                    + "onto the outbox. Each line shows the handler, the call chain, and the save it "
                    + "reaches");
        }
        finally
        {
            foreach (var module in modules)
            {
                module.Dispose();
            }
        }
    }

    /// <summary>
    /// The shortest call chain from any of the handler's methods to a save, or null when none exists
    /// within <paramref name="maxCallDepth"/>. Breadth-first with a visited set, so the walk is
    /// cycle-safe and reports the shortest path it can.
    /// </summary>
    private static string? FindSavePath(
        TypeDefinition handler,
        CallGraphIndex index,
        IReadOnlyCollection<string> allowedTypesAndNamespaces,
        int maxCallDepth)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<(MethodDefinition Method, int Depth, string Path)>();

        foreach (var method in handler.Methods.Where(m => visited.Add(m.FullName)))
        {
            pending.Enqueue((method, 0, Describe(method)));
        }

        while (pending.Count > 0)
        {
            var (method, depth, path) = pending.Dequeue();

            foreach (var callee in Callees(method, index))
            {
                if (IsSaveCall(callee))
                {
                    return $"{path} calls {SimpleTypeName(callee.DeclaringType)}.{callee.Name}";
                }

                if (depth < maxCallDepth)
                {
                    EnqueueTargets(callee, index, allowedTypesAndNamespaces, visited, pending, depth, path);
                }
            }
        }

        return null;
    }

    /// <summary>Queues the unvisited, non-allowlisted implementations one call could land on.</summary>
    private static void EnqueueTargets(
        MethodReference callee,
        CallGraphIndex index,
        IReadOnlyCollection<string> allowedTypesAndNamespaces,
        HashSet<string> visited,
        Queue<(MethodDefinition Method, int Depth, string Path)> pending,
        int depth,
        string path)
    {
        var targets = index.TargetsOf(callee)
            .Where(target => !IsAllowed(target.DeclaringType.FullName, allowedTypesAndNamespaces)
                && visited.Add(target.FullName));

        foreach (var target in targets)
        {
            pending.Enqueue((target, depth + 1, $"{path} -> {Describe(target)}"));
        }
    }

    /// <summary>Every method a method's IL references, its state machine's IL included.</summary>
    private static IEnumerable<MethodReference> Callees(MethodDefinition method, CallGraphIndex index)
    {
        foreach (var body in BodyMethods(method, index))
        {
            foreach (var instruction in body.Body.Instructions)
            {
                if (instruction.Operand is MethodReference callee)
                {
                    yield return callee;
                }
            }
        }
    }

    /// <summary>
    /// The methods that actually hold a method's logic: the method itself, plus every method of the
    /// compiler-generated state machine an <c>async</c> or iterator method rewrites its body into. The
    /// visible method only starts the machine, so a walk that stops there sees an empty handler.
    /// </summary>
    private static IEnumerable<MethodDefinition> BodyMethods(MethodDefinition method, CallGraphIndex index)
    {
        if (method.HasBody)
        {
            yield return method;
        }

        var stateMachine = StateMachineOf(method, index);
        if (stateMachine is null)
        {
            yield break;
        }

        foreach (var machineMethod in stateMachine.Methods.Where(m => m.HasBody))
        {
            yield return machineMethod;
        }
    }

    /// <summary>The state-machine type an async/iterator method compiles into, or null.</summary>
    private static TypeDefinition? StateMachineOf(MethodDefinition method, CallGraphIndex index)
    {
        var attribute = method.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName is "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                or "System.Runtime.CompilerServices.IteratorStateMachineAttribute");

        if (attribute is null || attribute.ConstructorArguments.Count == 0)
        {
            return null;
        }

        return attribute.ConstructorArguments[0].Value is TypeReference reference ? index.Find(reference) : null;
    }

    /// <summary>True when the callee flushes pending changes to the database.</summary>
    private static bool IsSaveCall(MethodReference callee)
    {
        if (!SaveMemberNames.Contains(callee.Name, StringComparer.Ordinal))
        {
            return false;
        }

        var declaringType = callee.DeclaringType;
        if (declaringType is null)
        {
            return false;
        }

        if (declaringType.FullName.StartsWith(EntityFrameworkNamespacePrefix, StringComparison.Ordinal))
        {
            return true;
        }

        var simpleName = SimpleTypeName(declaringType);
        return Array.Exists(SaveSurfaceTypeSuffixes, suffix => simpleName.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>The type's simple name with the generic-arity marker stripped.</summary>
    private static string SimpleTypeName(TypeReference type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }

    /// <summary>
    /// A readable node for the reported chain. Compiler-generated nested types (lambda closures, state
    /// machines) are collapsed onto the type that declares them, so the chain reads like source.
    /// </summary>
    private static string Describe(MethodDefinition method)
    {
        var declaringType = method.DeclaringType;
        while (declaringType.DeclaringType is not null && declaringType.Name.StartsWith('<'))
        {
            declaringType = declaringType.DeclaringType;
        }

        return $"{declaringType.FullName}.{method.Name}";
    }
}
