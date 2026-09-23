using Mono.Cecil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// The framework's one deliberate clock read below Infrastructure:
    /// <c>BaseDomainEvent.DateOccurred</c> defaults to the construction instant, because a domain
    /// event's occurrence IS the moment the aggregate raises it (the type's own remarks carry the
    /// reasoning). Every map that registers the framework Domain assembly would otherwise have to
    /// repeat this entry, so the rule exempts it itself.
    /// </summary>
    private const string DomainEventOccurrenceStamp = "MMCA.Common.Domain.DomainEvents.BaseDomainEvent";

    /// <summary>
    /// The ambient-clock getters Domain and Application code must not read, as
    /// (declaring type full name, getter name) pairs.
    /// </summary>
    private static readonly (string Type, string Getter)[] AmbientClockGetters =
    [
        ("System.DateTime", "get_UtcNow"),
        ("System.DateTime", "get_Now"),
        ("System.DateTimeOffset", "get_UtcNow"),
        ("System.DateTimeOffset", "get_Now"),
    ];

    /// <summary>
    /// Domain and Application code takes time as an input (an injected <see cref="TimeProvider"/> in
    /// a handler, an instant passed into an aggregate method) and never reads the ambient clock:
    /// <c>DateTime.UtcNow</c>, <c>DateTime.Now</c>, <c>DateTimeOffset.UtcNow</c> or
    /// <c>DateTimeOffset.Now</c>. A clock read buried in a business rule cannot be driven by a test,
    /// so every expiry, cutoff and "is it overdue yet" branch it guards is either untested or tested
    /// by sleeping. The rule fails on any call to those getters in the map's Domain and Application
    /// assemblies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> IL, through the Mono.Cecil NetArchTest already carries: every method body
    /// of every type (lambdas and async state machines included, since they are nested types of their
    /// own) is searched for a call to one of the four getters.
    /// </para>
    /// <para>
    /// <b>Allowlist entries</b> are a type full name, a namespace prefix, or one member written as
    /// <c>Namespace.Type.Member</c> (the source member name, so a clock read inside that member's
    /// lambda or async body is exempt with it). The framework's <c>BaseDomainEvent</c> occurrence
    /// stamp is always exempt.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypesAndMembers">
    /// Type full names, namespace prefixes or <c>Type.Member</c> entries where reading the clock is the
    /// deliberate, reviewed choice. An empty list exempts nothing beyond the framework's event stamp.
    /// </param>
    public static void DomainAndApplicationDoNotReadTheClock(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypesAndMembers)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypesAndMembers);

        IReadOnlyCollection<string> allowed = [DomainEventOccurrenceStamp, .. allowedTypesAndMembers];

        var locations = map.OfLayer(Layer.Domain)
            .Concat(map.OfLayer(Layer.Application))
            .Select(assembly => assembly.Location)
            .Where(location => !string.IsNullOrEmpty(location) && File.Exists(location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        locations.Should().NotBeEmpty(
            because: "the clock-read rule found no Domain or Application assemblies in the map, so it would verify nothing");

        var violations = new List<string>();
        foreach (var location in locations)
        {
            using var module = ModuleDefinition.ReadModule(location);

            violations.AddRange(module.GetTypes().SelectMany(type => ClockReadsIn(type, allowed)));
        }

        ArchitectureAssert.NoViolations(
            violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            "Domain and Application code takes time as an input and never reads the ambient clock: "
                + "inject TimeProvider into the handler and pass the instant into the domain method, so "
                + "a test can drive every time-dependent branch. Allowlist the type or Type.Member when "
                + "reading the clock there is the deliberate choice");
    }

    /// <summary>
    /// The disallowed clock reads inside one type's method bodies, each attributed to the type that
    /// owns it (a compiler-generated nested type answers for its declaring type) and to the source
    /// member that wrote it.
    /// </summary>
    private static IEnumerable<string> ClockReadsIn(TypeDefinition type, IReadOnlyCollection<string> allowed)
    {
        var owner = OwnerName(type);
        if (IsAllowed(owner, allowed))
        {
            yield break;
        }

        foreach (var method in type.Methods.Where(m => m.HasBody))
        {
            var member = SourceMemberName(type, method);
            if (allowed.Contains($"{owner}.{member}", StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var getter in ClockGettersCalledBy(method))
            {
                yield return $"  - {owner}.{member} reads {getter}";
            }
        }
    }

    /// <summary>The ambient-clock getters a method body calls, as <c>Type.Property</c> display names.</summary>
    private static IEnumerable<string> ClockGettersCalledBy(MethodDefinition method) =>
        method.Body.Instructions
            .Select(instruction => instruction.Operand as MethodReference)
            .Where(callee => callee is not null
                && AmbientClockGetters.Any(getter =>
                    string.Equals(callee.Name, getter.Getter, StringComparison.Ordinal)
                    && string.Equals(callee.DeclaringType.FullName, getter.Type, StringComparison.Ordinal)))
            .Select(callee => $"{callee!.DeclaringType.Name}.{callee.Name["get_".Length..]}");

    /// <summary>
    /// The member a developer wrote that a method's body belongs to: the method itself, or, for the
    /// compiler's rewrites, the member named inside the angle brackets of the generated name (an async
    /// or iterator state machine <c>&lt;Member&gt;d__N</c>, a lambda <c>&lt;Member&gt;b__N_M</c>).
    /// </summary>
    private static string SourceMemberName(TypeDefinition type, MethodDefinition method)
    {
        var current = type;
        while (current is not null && current.Name.StartsWith('<'))
        {
            if (BracketedName(current.Name) is { Length: > 0 } fromType)
            {
                return fromType;
            }

            current = current.DeclaringType;
        }

        return BracketedName(method.Name) is { Length: > 0 } fromMethod ? fromMethod : method.Name;
    }

    /// <summary>The text between a leading <c>&lt;</c> and its <c>&gt;</c>, or null when the name has none.</summary>
    private static string? BracketedName(string name)
    {
        if (!name.StartsWith('<'))
        {
            return null;
        }

        var close = name.IndexOf('>', StringComparison.Ordinal);
        return close > 1 ? name[1..close] : null;
    }
}
