namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>Shared DTO contracts are immutable (no public, non-init setters).</summary>
    public static void DtosAreImmutable(IArchitectureMap map)
    {
        var offenders = map.ModuleShared()
            .SelectMany(a => a.GetLoadableTypes())
            .Where(ImplementsBaseDto)
            .SelectMany(MutablePropertyViolations);

        ArchitectureAssert.NoViolations(offenders,
            "DTOs must be immutable — use init-only properties so a contract cannot be mutated after construction");
    }

    /// <summary>Command and query message types are immutable (no public, non-init setters).</summary>
    public static void CommandsAndQueriesAreImmutable(IArchitectureMap map)
    {
        var messageTypes = map.ModuleApplication()
            .SelectMany(a => a.ConcreteClasses())
            .SelectMany(h => h.GetInterfaces())
            .Where(i => i.IsGenericType && i.Name is "ICommandHandler`2" or "IQueryHandler`2")
            .Select(i => i.GetGenericArguments()[0])
            .Distinct();

        var offenders = messageTypes.SelectMany(MutablePropertyViolations);

        ArchitectureAssert.NoViolations(offenders,
            "command/query messages must be immutable — use init-only properties");
    }

    /// <summary>Domain events are immutable (no public, non-init setters).</summary>
    public static void DomainEventsAreImmutable(IArchitectureMap map)
    {
        var offenders = map.ModuleDomain()
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.HasBaseTypeStartingWith("MMCA.Common.Domain.DomainEvents.BaseDomainEvent"))
            .SelectMany(MutablePropertyViolations);

        ArchitectureAssert.NoViolations(offenders, "domain events must be immutable — use init-only properties");
    }

    /// <summary>Integration events are immutable (no public, non-init setters).</summary>
    public static void IntegrationEventsAreImmutable(IArchitectureMap map)
    {
        var offenders = map.Layers.Select(l => l.Assembly).Distinct()
            .SelectMany(a => a.ConcreteClasses())
            .Where(IsIntegrationEvent)
            .SelectMany(MutablePropertyViolations);

        ArchitectureAssert.NoViolations(offenders, "integration events must be immutable — use init-only properties");
    }

    /// <summary>Value objects are sealed, immutable, and reside in the Shared layer.</summary>
    public static void ValueObjectsAreImmutableSealedInShared(IArchitectureMap map)
    {
        var sharedAssemblies = map.OfLayer(Layer.Shared).ToHashSet();
        var valueObjects = map.Layers.Select(l => l.Assembly).Distinct()
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.HasBaseTypeStartingWith("MMCA.Common.Shared.ValueObjects.ValueObject"))
            .ToList();

        var notSealed = valueObjects.Where(t => !t.IsSealed)
            .Select(t => $"  - {t.FullName} (value object must be sealed)");

        var notInShared = valueObjects.Where(t => !sharedAssemblies.Contains(t.Assembly))
            .Select(t => $"  - {t.FullName} (value object must reside in the Shared layer)");

        var mutable = valueObjects.SelectMany(MutablePropertyViolations);

        ArchitectureAssert.NoViolations(notSealed.Concat(notInShared).Concat(mutable),
            "value objects must be sealed, immutable, and reside in the Shared layer");
    }

    private static IEnumerable<string> MutablePropertyViolations(Type type) =>
        type.DeclaredPublicProperties()
            .Where(p => p.HasPublicMutableSetter())
            .Select(p => $"  - {type.FullName}.{p.Name} has a public mutable setter");
}
