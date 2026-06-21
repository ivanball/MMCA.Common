namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>CQRS handlers end in "Handler" and are sealed.</summary>
    public static void HandlersAreSealedWithHandlerSuffix(IArchitectureMap map)
    {
        var offenders = map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses())
            .Where(IsHandler)
            .Where(t => !t.SimpleName().EndsWith("Handler", StringComparison.Ordinal) || !t.IsSealed)
            .Select(t => $"  - {t.FullName} (handlers must end in 'Handler' and be sealed)");

        ArchitectureAssert.NoViolations(offenders, "command/query handlers must end in 'Handler' and be sealed");
    }

    /// <summary>Command message types (the <c>TCommand</c> of a command handler) end in "Command" or "Request".</summary>
    public static void CommandsHaveCommandOrRequestSuffix(IArchitectureMap map) =>
        AssertMessageSuffix(map, "ICommandHandler`2", ["Command", "Request"], "command");

    /// <summary>Query message types (the <c>TQuery</c> of a query handler) end in "Query".</summary>
    public static void QueriesHaveQuerySuffix(IArchitectureMap map) =>
        AssertMessageSuffix(map, "IQueryHandler`2", ["Query"], "query");

    /// <summary>FluentValidation validators end in "Validator" or "Rules".</summary>
    public static void ValidatorsHaveValidatorOrRulesSuffix(IArchitectureMap map)
    {
        var offenders = map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.HasBaseTypeStartingWith("FluentValidation.AbstractValidator"))
            .Where(t => !t.SimpleName().EndsWith("Validator", StringComparison.Ordinal)
                && !t.SimpleName().EndsWith("Rules", StringComparison.Ordinal))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders, "validators must end in 'Validator' or 'Rules'");
    }

    /// <summary>Shared DTO contracts end in "DTO" or "Lookup".</summary>
    public static void SharedDtosHaveDtoOrLookupSuffix(IArchitectureMap map)
    {
        var offenders = map.ModuleShared()
            .SelectMany(a => a.GetLoadableTypes())
            .Where(ImplementsBaseDto)
            .Where(t => !t.SimpleName().EndsWith("DTO", StringComparison.Ordinal)
                && !t.SimpleName().EndsWith("Lookup", StringComparison.Ordinal))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders, "shared DTO contracts must end in 'DTO' or 'Lookup'");
    }

    /// <summary>Domain events are sealed and live in a <c>*.DomainEvents</c> namespace.</summary>
    public static void DomainEventsAreSealedInDomainEventsNamespace(IArchitectureMap map)
    {
        var offenders = map.ModuleDomain()
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.HasBaseTypeStartingWith("MMCA.Common.Domain.DomainEvents.BaseDomainEvent"))
            .Where(t => !t.IsSealed || t.Namespace?.Contains(".DomainEvents", StringComparison.Ordinal) != true)
            .Select(t => $"  - {t.FullName} (domain events must be sealed and in a *.DomainEvents namespace)");

        ArchitectureAssert.NoViolations(offenders, "domain events must be sealed and reside in a *.DomainEvents namespace");
    }

    /// <summary>Invariant classes (name ends "Invariants") are static (abstract + sealed).</summary>
    public static void InvariantClassesAreStatic(IArchitectureMap map)
    {
        var offenders = map.ModuleDomain()
            .SelectMany(a => a.GetLoadableTypes())
            .Where(t => t.IsClass && t.SimpleName().EndsWith("Invariants", StringComparison.Ordinal))
            .Where(t => !(t.IsAbstract && t.IsSealed))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders, "invariant classes must be static (abstract + sealed)");
    }

    /// <summary>EF entity configurations end in "Configuration", are sealed, and live in Infrastructure.</summary>
    public static void EfConfigurationsAreSealedWithConfigurationSuffix(IArchitectureMap map)
    {
        var offenders = map.Infrastructure()
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.Name == "IEntityTypeConfiguration`1"))
            .Where(t => !t.SimpleName().EndsWith("Configuration", StringComparison.Ordinal) || !t.IsSealed)
            .Select(t => $"  - {t.FullName} (EF configurations must end in 'Configuration' and be sealed)");

        ArchitectureAssert.NoViolations(offenders, "EF entity configurations must end in 'Configuration' and be sealed");
    }

    /// <summary>Specifications end in "Specification" and are sealed.</summary>
    public static void SpecificationsAreSealedWithSpecificationSuffix(IArchitectureMap map)
    {
        var offenders = map.Layers
            .Where(l => l.Layer is Layer.Domain or Layer.Application)
            .SelectMany(l => l.Assembly.ConcreteClasses())
            .Where(t => t.HasBaseTypeStartingWith("MMCA.Common.Domain.Specifications.Specification"))
            .Where(t => !t.SimpleName().EndsWith("Specification", StringComparison.Ordinal) || !t.IsSealed)
            .Select(t => $"  - {t.FullName} (specifications must end in 'Specification' and be sealed)");

        ArchitectureAssert.NoViolations(offenders, "specifications must end in 'Specification' and be sealed");
    }

    /// <summary>Repository implementations end in "Repository" and live in Infrastructure.</summary>
    public static void RepositoriesHaveRepositorySuffix(IArchitectureMap map)
    {
        var offenders = map.Infrastructure()
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                && i.Name is "IRepository`2" or "IReadRepository`2" or "IWriteRepository`2"))
            .Where(t => !t.SimpleName().EndsWith("Repository", StringComparison.Ordinal)
                && !t.SimpleName().EndsWith("Decorator", StringComparison.Ordinal))
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(offenders,
            "repository implementations must end in 'Repository' (or 'Decorator' for repository decorators)");
    }

    private static void AssertMessageSuffix(IArchitectureMap map, string handlerInterfaceName, string[] suffixes, string kind)
    {
        var offenders = new List<string>();

        foreach (var handler in map.ModuleApplication().SelectMany(a => a.ConcreteClasses()))
        {
            foreach (var handlerInterface in handler.GetInterfaces()
                .Where(i => i.IsGenericType && i.Name == handlerInterfaceName))
            {
                var messageType = handlerInterface.GetGenericArguments()[0];
                if (!suffixes.Any(s => messageType.SimpleName().EndsWith(s, StringComparison.Ordinal)))
                {
                    offenders.Add($"  - {messageType.FullName} (handled by {handler.Name})");
                }
            }
        }

        ArchitectureAssert.NoViolations(offenders.Distinct(StringComparer.Ordinal),
            $"{kind} message types must end in {string.Join(" or ", suffixes.Select(s => $"'{s}'"))}");
    }
}
