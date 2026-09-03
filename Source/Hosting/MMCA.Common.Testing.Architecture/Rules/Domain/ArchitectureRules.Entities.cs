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
    /// The factory convention holds across the WHOLE domain model, not just aggregate roots: any concrete
    /// type in the Domain or Shared layer that exposes a public static <c>Create(...)</c> must have at least
    /// one <c>Create</c> overload returning <c>Result&lt;TSelf&gt;</c>. This generalizes
    /// <see cref="AggregateRootsHaveResultFactory"/> from aggregate roots to value objects (e.g. <c>Money</c>,
    /// <c>Email</c>, <c>DateRange</c>), locking in the "factories always return Result&lt;T&gt;" convention so a
    /// future bare-entity/bare-value-object factory fails the build. Types that expose no <c>Create</c> are
    /// unaffected (construction sugar such as <c>Money.Zero()</c> / arithmetic operators is out of scope: only
    /// the <c>Create</c> factory name is governed).
    /// </summary>
    public static void DomainFactoriesReturnResult(IArchitectureMap map)
    {
        var violations = new List<string>();

        var factoryTypes = map.OfLayer(Layer.Domain)
            .Concat(map.OfLayer(Layer.Shared))
            .SelectMany(a => a.ConcreteClasses);

        foreach (var type in factoryTypes)
        {
            var createMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => string.Equals(m.Name, "Create", StringComparison.Ordinal))
                .ToList();

            if (createMethods.Count == 0)
            {
                continue;
            }

            if (!createMethods.Exists(m => ReturnsResultOf(m.ReturnType, type)))
            {
                violations.Add($"  - {type.FullName}: Create(...) must return Result<{type.Name}>");
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "every domain/value-object factory named Create must return Result<T> (factory convention normalized across the model)");
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

    /// <summary>
    /// Every public instance property on a module domain entity is closed for outside assignment: it
    /// has no setter, an <c>init</c>-only setter, or a non-public one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> An aggregate that hands out public setters is a data bag with a factory bolted on.
    /// The static <c>Create(...)</c> factory and its invariants can only guarantee the state they
    /// build; a public setter lets any caller move the aggregate to a state no invariant ever saw,
    /// which is exactly the encapsulation the DDD rules in this file exist to hold. Mutation
    /// therefore goes through NAMED domain methods on the aggregate (<c>Rename</c>, <c>Deactivate</c>,
    /// <c>SetPrice</c>), which is also where the domain event belongs, so a state change and its
    /// announcement cannot drift apart.
    /// </para>
    /// <para>
    /// <b>What counts as compliant.</b> A get-only property, a computed property, an
    /// <c>init</c>-only property (construction-time assignment is what the factory already governs),
    /// and any property whose setter is <see langword="private"/>, <see langword="protected"/> or
    /// <see langword="internal"/>. Only a
    /// genuinely public, non-<c>init</c> setter is a violation. Navigation properties are included
    /// rather than exempted: assigning a child collection or a related aggregate from outside is the
    /// same invariant hole, so navigation assignment goes through a <c>SetXxx</c> method too (the
    /// framework's <c>SetItems&lt;T&gt;</c> is that method for the collection case).
    /// </para>
    /// <para>
    /// <b>Limits.</b> Reflection sees accessor visibility, not intent, so a public setter that is
    /// only ever called from inside the aggregate still fails: make it private and the compiler
    /// agrees. The rule scopes to <see cref="IArchitectureMap.ModuleDomain"/> and to types inheriting
    /// the auditable entity base, the same filter <see cref="DomainEntitiesAreSealed"/> uses, so DTOs,
    /// value objects and framework base entities are out of scope, and the rule is vacuous in a
    /// module-less framework repo. Fields are not inspected: the entity model is property-based, and a
    /// public mutable field is already caught by the analyzer set.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    public static void EntityPropertySettersAreNonPublic(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = map.ModuleDomain()
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => t.InheritsAuditableEntity)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.HasPublicMutableSetter)
                .Select(p => $"  - {t.Name}.{p.Name}"));

        ArchitectureAssert.NoViolations(violations,
            "domain entity properties must not have a public setter: mutation goes through named "
                + "domain methods (including SetXxx for navigations), so no caller can move an "
                + "aggregate to a state its invariants never validated");
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
