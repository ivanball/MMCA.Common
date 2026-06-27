namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Vertical-slice cohesion (rubric §5): a use-case slice keeps its command/query and the handler
    /// that serves it in the <em>same</em> namespace (the <c>…/UseCases/{Action}/</c> folder), so a
    /// feature is one cohesive unit rather than a handler stranded in a horizontal <c>Handlers/</c>
    /// folder away from its contract. Only same-assembly contracts are checked — a module that reuses
    /// a framework-generic command (e.g. <c>DeleteEntityCommand&lt;,&gt;</c> from MMCA.Common) is
    /// legitimately not co-located with it, so cross-assembly contracts are exempt.
    /// </summary>
    public static void HandlersAreCoLocatedWithTheirContracts(IArchitectureMap map)
    {
        var offenders = new List<string>();

        foreach (var handler in map.OfLayer(Layer.Application).SelectMany(a => a.ConcreteClasses()).Where(IsHandler))
        {
            var contract = HandlerContract(handler);
            if (contract is null || !IsSameAssemblyConcreteContract(contract, handler))
            {
                continue;
            }

            if (!NamespacesMatch(contract, handler))
            {
                offenders.Add($"  - {handler.FullName}: handles {contract.SimpleName()} in '{contract.Namespace}' but lives in '{handler.Namespace}'");
            }
        }

        ArchitectureAssert.NoViolations(offenders,
            "a use-case slice's command/query and its handler must live in the same namespace (vertical-slice cohesion)");
    }

    /// <summary>
    /// Vertical-slice cohesion (rubric §5): a FluentValidation validator lives in the same namespace
    /// as the command/query/request it validates, so the slice's validation stays inside the slice.
    /// Only same-assembly validated types are checked — validators over Shared request DTOs or generic
    /// type parameters are exempt (the validated type is, by design, not in the Application slice).
    /// </summary>
    public static void ValidatorsAreCoLocatedWithTheirContracts(IArchitectureMap map)
    {
        var offenders = new List<string>();

        foreach (var validator in map.OfLayer(Layer.Application).SelectMany(a => a.ConcreteClasses()))
        {
            var validated = ValidatedType(validator);
            if (validated is null || !IsSameAssemblyConcreteContract(validated, validator))
            {
                continue;
            }

            if (!NamespacesMatch(validated, validator))
            {
                offenders.Add($"  - {validator.FullName}: validates {validated.SimpleName()} in '{validated.Namespace}' but lives in '{validator.Namespace}'");
            }
        }

        ArchitectureAssert.NoViolations(offenders,
            "a validator must live in the same namespace as the command/query it validates (vertical-slice cohesion)");
    }

    /// <summary>The command/query type a handler serves (the first generic arg of its handler interface).</summary>
    private static Type? HandlerContract(Type handler)
    {
        var handlerInterface = handler.GetInterfaces().FirstOrDefault(IsHandlerInterface);
        return handlerInterface?.GetGenericArguments().FirstOrDefault();
    }

    /// <summary>The type a FluentValidation <c>AbstractValidator&lt;T&gt;</c> validates, or null if it is not a validator.</summary>
    private static Type? ValidatedType(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType
                && baseType.GetGenericTypeDefinition().FullName == "FluentValidation.AbstractValidator`1")
            {
                return baseType.GetGenericArguments().FirstOrDefault();
            }
        }

        return null;
    }

    /// <summary>A contract worth a co-location check: a concrete (non-generic-parameter) type from the same assembly.</summary>
    private static bool IsSameAssemblyConcreteContract(Type contract, Type owner) =>
        !contract.IsGenericParameter && contract.Assembly == owner.Assembly;

    private static bool NamespacesMatch(Type a, Type b) =>
        string.Equals(a.Namespace, b.Namespace, StringComparison.Ordinal);
}
