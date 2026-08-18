using System.Runtime.CompilerServices;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Every public asynchronous method on a public Application- or Infrastructure-layer type must accept
    /// a <see cref="CancellationToken"/> as its LAST parameter, named <c>cancellationToken</c>. An async
    /// method that cannot be cancelled is a request that outlives its caller: a cancelled HTTP request, an
    /// expired CQRS timeout budget or a stopping host all leave the work running against the database, and
    /// the uniform trailing position plus name is what lets the decorator pipeline, the repositories and
    /// generated clients pass the linked token through mechanically instead of case by case.
    /// <para>
    /// <b>Scope:</b> methods returning <c>Task</c>, <c>Task&lt;T&gt;</c>, <c>ValueTask</c> or
    /// <c>ValueTask&lt;T&gt;</c>, declared (not inherited) as public members of a publicly-visible type in
    /// a <see cref="Layer.Application"/> or <see cref="Layer.Infrastructure"/> assembly. A method with no
    /// parameters is NOT excused: the token would simply be its only parameter.
    /// </para>
    /// <para>
    /// <b>Automatic exemptions</b> (the signature is not the repo's to change):
    /// <c>Dispose</c>/<c>DisposeAsync</c>, compiler-generated and special-name members (property
    /// accessors, operators, event accessors), members of delegate types, an override of a base method
    /// declared outside the map's assemblies, and an implicit implementation of an interface method
    /// declared outside the map's assemblies (<c>IHostedService.StartAsync</c>, <c>IHealthCheck</c>,
    /// framework middleware, and so on).
    /// </para>
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="exemptMethods">
    /// Additional exemptions in <c>"TypeName.MethodName"</c> form (simple type name, generic arity
    /// stripped). Use only where adding the parameter would be a breaking change to a shipped public API,
    /// and record the reason next to the entry.
    /// </param>
    public static void AsyncMethodsDeclareTrailingCancellationToken(
        IArchitectureMap map,
        IReadOnlyCollection<string>? exemptMethods = null)
    {
        ArgumentNullException.ThrowIfNull(map);

        var exempt = new HashSet<string>(exemptMethods ?? [], StringComparer.Ordinal);
        var mapAssemblies = map.Layers.Select(l => l.Assembly).ToHashSet();
        var violations = new List<string>();

        var assemblies = map.OfLayer(Layer.Application)
            .Concat(map.OfLayer(Layer.Infrastructure))
            .Distinct();

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.LoadableTypes.Where(IsCandidateType))
            {
                CheckType(type, mapAssemblies, exempt, violations);
            }
        }

        ArchitectureAssert.NoViolations(
            violations,
            "public async methods on Application/Infrastructure types must take a trailing " +
            "'CancellationToken cancellationToken' - work that cannot be cancelled keeps running against " +
            "the database after its caller is gone, and only a uniform trailing token lets the decorator " +
            "pipeline and repositories forward the linked token mechanically");
    }

    private static void CheckType(
        Type type,
        HashSet<Assembly> mapAssemblies,
        HashSet<string> exempt,
        List<string> violations)
    {
        var externallyFixed = ExternallyFixedMethods(type, mapAssemblies);

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (!IsCandidateMethod(method)
                || externallyFixed.Contains(method)
                || exempt.Contains($"{type.SimpleName}.{method.Name}"))
            {
                continue;
            }

            var problem = TokenProblem(method);
            if (problem is not null)
            {
                violations.Add($"  - {type.FullName}.{method.Name}: {problem}");
            }
        }
    }

    /// <summary>Publicly-visible, non-delegate, non-compiler-generated types.</summary>
    private static bool IsCandidateType(Type type) =>
        type.IsVisible
        && !type.IsSubclassOf(typeof(MulticastDelegate))
        && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static bool IsCandidateMethod(MethodInfo method) =>
        method.IsPublic
        && !method.IsSpecialName
        && !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && !string.Equals(method.Name, "Dispose", StringComparison.Ordinal)
        && !string.Equals(method.Name, "DisposeAsync", StringComparison.Ordinal)
        && IsAwaitableReturn(method.ReturnType);

    private static bool IsAwaitableReturn(Type returnType)
    {
        var definition = returnType.IsGenericType ? returnType.GetGenericTypeDefinition() : returnType;
        return definition == typeof(Task)
            || definition == typeof(Task<>)
            || definition == typeof(ValueTask)
            || definition == typeof(ValueTask<>);
    }

    /// <summary>The problem with the method's token parameter, or null when it is correct.</summary>
    private static string? TokenProblem(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var last = parameters.Length == 0 ? null : parameters[^1];

        if (last is not null
            && last.ParameterType == typeof(CancellationToken)
            && string.Equals(last.Name, "cancellationToken", StringComparison.Ordinal))
        {
            return null;
        }

        if (Array.Exists(parameters, p => p.ParameterType == typeof(CancellationToken)))
        {
            return last?.ParameterType == typeof(CancellationToken)
                ? $"the trailing CancellationToken must be named 'cancellationToken' (found '{last.Name}')"
                : "the CancellationToken must be the LAST parameter";
        }

        return "no CancellationToken parameter";
    }

    /// <summary>
    /// The methods whose signature the repo cannot change: overrides of a base method declared outside the
    /// map, and implicit implementations of an interface method declared outside the map.
    /// </summary>
    private static HashSet<MethodInfo> ExternallyFixedMethods(Type type, HashSet<Assembly> mapAssemblies)
    {
        var fixedMethods = new HashSet<MethodInfo>();

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var baseDefinition = method.GetBaseDefinition();
            if (baseDefinition.DeclaringType is { } declaring
                && declaring != type
                && !mapAssemblies.Contains(declaring.Assembly))
            {
                fixedMethods.Add(method);
            }
        }

        if (type.IsInterface)
        {
            return fixedMethods;
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (mapAssemblies.Contains(contract.Assembly))
            {
                continue;
            }

            foreach (var target in InterfaceTargets(type, contract))
            {
                fixedMethods.Add(target);
            }
        }

        return fixedMethods;
    }

    private static IEnumerable<MethodInfo> InterfaceTargets(Type type, Type contract)
    {
        try
        {
            return [.. type.GetInterfaceMap(contract).TargetMethods];
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TypeLoadException)
        {
            // A generic-parameter or otherwise unmappable interface: nothing to exempt.
            return [];
        }
    }
}
