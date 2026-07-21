namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>CQRS handlers live only in Application — never in Domain or Infrastructure.</summary>
    public static void HandlersResideInApplicationLayer(IArchitectureMap map)
    {
        var offenders = map.ModuleDomain().Concat(map.Infrastructure())
            .SelectMany(a => a.ConcreteClasses)
            .Where(IsHandler)
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "command/query handlers must reside in the Application layer, not Domain or Infrastructure");
    }

    /// <summary>A handler must not inject another handler — compose at the controller/orchestrator level.</summary>
    public static void HandlersDoNotInjectOtherHandlers(IArchitectureMap map)
    {
        var offenders = map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses)
            .Where(IsHandler)
            .Where(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => IsHandlerParameter(p.ParameterType)))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "a handler must not inject another handler — compose at the controller/orchestrator level instead");
    }

    /// <summary>Non-handler Application services must not broker commands by injecting a handler.</summary>
    public static void ApplicationServicesDoNotInjectHandlers(IArchitectureMap map)
    {
        var offenders = map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => !IsHandler(t))
            .Where(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => IsHandlerParameter(p.ParameterType)))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "application services must not inject command/query handlers — dispatch them from the API/controller layer");
    }

    /// <summary>God-class guardrail: no <c>*Service</c> constructor may exceed the dependency ceiling.</summary>
    public static void ApplicationServicesRespectConstructorArity(IArchitectureMap map, int maxParameters = 8)
    {
        var offenders = new List<string>();

        foreach (var type in map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.SimpleName.EndsWith("Service", StringComparison.Ordinal)))
        {
            var ctors = type.GetConstructors();
            if (ctors.Length == 0)
            {
                continue;
            }

            var max = ctors.Max(c => c.GetParameters().Length);
            if (max > maxParameters)
            {
                offenders.Add($"  - {type.FullName}: {max} constructor parameters (max {maxParameters})");
            }
        }

        ArchitectureAssert.NoViolations(offenders,
            $"application services must not exceed {maxParameters} constructor dependencies (god-class threshold) — decompose");
    }

    /// <summary>FluentValidation validators live only in Application — never Domain or Infrastructure.</summary>
    public static void ValidatorsResideInApplicationLayer(IArchitectureMap map)
    {
        var offenders = map.ModuleDomain().Concat(map.Infrastructure())
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.HasBaseTypeStartingWith("FluentValidation.AbstractValidator"))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "FluentValidation validators must reside in the Application layer, not Domain or Infrastructure");
    }

    /// <summary>Domain event handlers reside in Application and are sealed — never in the Domain layer.</summary>
    public static void DomainEventHandlersResideInApplicationAndSealed(IArchitectureMap map)
    {
        var misplaced = map.ModuleDomain()
            .SelectMany(a => a.ConcreteClasses)
            .Where(IsEventHandler)
            .Select(t => $"  - {t.FullName} (event handler must not be in Domain)");

        var unsealed = map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => IsEventHandler(t) && !t.IsSealed)
            .Select(t => $"  - {t.FullName} (event handler must be sealed)");

        ArchitectureAssert.NoViolations(misplaced.Concat(unsealed),
            "domain/integration event handlers must reside in Application and be sealed");
    }

    private static bool IsHandlerInterface(Type i) =>
        i.IsGenericType && i.Name is "ICommandHandler`2" or "IQueryHandler`2";

    private static bool IsHandler(Type type) =>
        type.GetInterfaces().Any(IsHandlerInterface);

    private static bool IsHandlerParameter(Type type) =>
        IsHandlerInterface(type) || type.GetInterfaces().Any(IsHandlerInterface);

    private static bool IsEventHandler(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType
            && i.Name is "IDomainEventHandler`1" or "IIntegrationEventHandler`1");
}
