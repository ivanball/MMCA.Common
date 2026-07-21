namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string ResultOpenGenericFullName = "MMCA.Common.Shared.Abstractions.Result`1";

    /// <summary>The Domain layer must actually contain aggregate roots, or the DDD suite is vacuous.</summary>
    public static void DomainExposesAggregateRoots(IArchitectureMap map)
    {
        var roots = map.OfLayer(Layer.Domain)
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.InheritsAggregateRoot);

        roots.Should().NotBeEmpty(
            because: "the aggregate-root reflection filter must find roots, or the DDD fitness suite is vacuous");
    }

    /// <summary>Every aggregate root is built via a static <c>Create(...)</c> returning <c>Result&lt;TAggregate&gt;</c>.</summary>
    public static void AggregateRootsHaveResultFactory(IArchitectureMap map)
    {
        var violations = new List<string>();

        foreach (var type in map.OfLayer(Layer.Domain).SelectMany(a => a.ConcreteClasses).Where(t => t.InheritsAggregateRoot))
        {
            var createMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => string.Equals(m.Name, "Create", StringComparison.Ordinal))
                .ToList();

            if (createMethods.Count == 0)
            {
                violations.Add($"  - {type.FullName}: no public static Create(...) factory");
            }
            else if (!createMethods.Exists(m => ReturnsResultOf(m.ReturnType, type)))
            {
                violations.Add($"  - {type.FullName}: Create(...) must return Result<{type.Name}>");
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "every aggregate root must be constructed via a static Create factory returning Result<T> (DDD convention)");
    }

    /// <summary>
    /// Aggregate roots across the WHOLE Domain layer (framework + module) expose no public constructor —
    /// construction goes through the static <c>Create(...)</c> factory. This is the minimal-base
    /// counterpart to <see cref="AggregateRootsHaveNoPublicConstructors"/>, which scopes to per-module
    /// domains only and so is vacuous in a module-less framework repo (MMCA.Common). Pairs with
    /// <see cref="AggregateRootsHaveResultFactory"/> to fully pin the private-ctor + Result-factory
    /// construction invariant.
    /// </summary>
    public static void DomainAggregateRootsHaveNoPublicConstructors(IArchitectureMap map)
    {
        var violations = map.OfLayer(Layer.Domain)
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.InheritsAggregateRoot
                && t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0)
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(violations,
            "aggregate roots must have no public constructor — use the static Create(...) factory");
    }

    /// <summary>Module domain entities are sealed (prevents unintended inheritance of an aggregate).</summary>
    public static void DomainEntitiesAreSealed(IArchitectureMap map)
    {
        var violations = map.ModuleDomain()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.InheritsAuditableEntity && !t.IsSealed)
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(violations,
            "domain entities must be sealed — only abstract framework base entities are inheritable");
    }

    /// <summary>Aggregate roots expose no public constructor — construction goes through the factory.</summary>
    public static void AggregateRootsHaveNoPublicConstructors(IArchitectureMap map)
    {
        var violations = map.ModuleDomain()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.InheritsAggregateRoot
                && t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length > 0)
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(violations,
            "aggregate roots must have no public constructor — use the static Create(...) factory");
    }

    /// <summary>Auditable domain entities live only in Domain — never Application or Infrastructure.</summary>
    public static void EntitiesResideInDomainLayer(IArchitectureMap map)
    {
        Layer[] nonDomain = [Layer.Application, Layer.Infrastructure];
        var violations = map.Layers
            .Where(l => nonDomain.Contains(l.Layer))
            .SelectMany(l => l.Assembly.ConcreteClasses)
            .Where(t => t.InheritsAuditableEntity)
            .Select(t => $"  - {t.FullName}");

        ArchitectureAssert.NoViolations(violations,
            "domain entities must reside in the Domain layer, not Application or Infrastructure");
    }

    /// <summary>
    /// DTOs do not leak into Domain or Infrastructure, and request models do not leak into Domain. A
    /// <c>*Request</c> type IS allowed in Infrastructure — an outbound HTTP-client payload for an
    /// external API is an infrastructure concern, not a public contract — matching the established
    /// convention.
    /// </summary>
    public static void DtosAndRequestsAreNotInDomainOrInfrastructure(IArchitectureMap map)
    {
        var domainViolations = map.OfLayer(Layer.Domain)
            .SelectMany(a => a.LoadableTypes)
            .Where(t => t is { IsClass: true } or { IsValueType: true })
            .Where(t => t.SimpleName.EndsWith("DTO", StringComparison.Ordinal)
                || t.SimpleName.EndsWith("Request", StringComparison.Ordinal)
                || ImplementsBaseDto(t))
            .Select(t => $"  - {t.FullName} (DTO/request in Domain)");

        var infrastructureViolations = map.Infrastructure()
            .SelectMany(a => a.LoadableTypes)
            .Where(t => t is { IsClass: true } or { IsValueType: true })
            .Where(t => t.SimpleName.EndsWith("DTO", StringComparison.Ordinal) || ImplementsBaseDto(t))
            .Select(t => $"  - {t.FullName} (DTO in Infrastructure)");

        ArchitectureAssert.NoViolations(domainViolations.Concat(infrastructureViolations),
            "DTOs belong in Shared (not Domain/Infrastructure); request models belong in Application/Shared (not Domain)");
    }

    private static bool ReturnsResultOf(Type returnType, Type aggregateType) =>
        returnType.IsGenericType
        && string.Equals(returnType.GetGenericTypeDefinition().FullName, ResultOpenGenericFullName, StringComparison.Ordinal)
        && returnType.GetGenericArguments()[0] == aggregateType;

    private static bool ImplementsBaseDto(Type type) =>
        type.GetInterfaces().Any(i => i.Name.StartsWith("IBaseDTO", StringComparison.Ordinal));
}
