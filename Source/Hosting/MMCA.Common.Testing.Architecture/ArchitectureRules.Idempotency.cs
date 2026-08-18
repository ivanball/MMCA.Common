namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The attribute that opts an action into the <c>Idempotency-Key</c> replay contract.</summary>
    private const string IdempotentAttributeName = "IdempotentAttribute";

    /// <summary>The attribute that records a deliberate, justified opt-OUT of that contract.</summary>
    private const string NonIdempotentAttributeName = "NonIdempotentAttribute";

    /// <summary>The MVC attribute that makes an action answer POST.</summary>
    private const string HttpPostAttributeName = "HttpPostAttribute";

    /// <summary>
    /// Every POST action in the API layer states its idempotency intent: either
    /// <c>[Idempotent]</c> (the <c>Idempotency-Key</c> filter deduplicates retries) or
    /// <c>[NonIdempotent("...")]</c> (a recorded, justified decision to stay outside that contract).
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <remarks>
    /// <para>
    /// POST is the one verb HTTP does not define as idempotent, so a client that retries a timed-out
    /// POST cannot know whether the first attempt landed. The framework's answer is the
    /// <c>Idempotency-Key</c> filter, and it costs an existing client nothing to attach, because the
    /// filter no-ops for a request that carries no key header. That makes the ONLY failure mode worth
    /// gating an omission nobody noticed: an action that should replay but silently does not. This
    /// rule turns that omission into a build failure and leaves the opt-out available, as long as it
    /// is written down.
    /// </para>
    /// <para>
    /// Attributes are read with <c>inherit: true</c>, and a derived controller that simply inherits a
    /// framework base action reflects the BASE's method, so a concrete controller inheriting
    /// <c>AuthControllerBase</c> or <c>AggregateRootEntityControllerBase</c> already satisfies the rule
    /// through those bases. Abstract controller types are skipped: they are the declaration site, and
    /// their concrete subclasses are what actually route.
    /// </para>
    /// <para>
    /// Detection is by attribute type NAME, keeping this package free of an ASP.NET reference exactly
    /// as the rest of the rule library is. Only <c>[HttpPost]</c> is recognised: an action routed
    /// through <c>[AcceptVerbs("POST")]</c> or a conventional route is out of scope, since neither
    /// appears anywhere in this framework or its consumers.
    /// </para>
    /// </remarks>
    public static void PostActionsDeclareIdempotencyIntent(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = map.OfLayer(Layer.Api)
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => !IsCompilerGenerated(t))
            .Where(IsController)
            .SelectMany(UndeclaredPostActions)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        ArchitectureAssert.NoViolations(
            violations,
            "every POST action must declare its idempotency intent: add [Idempotent] so a retried "
            + "request replays the original response instead of writing twice, or [NonIdempotent(\"why\")] "
            + "when replaying would be wrong (token issuance, revocation, single-use code exchange)");
    }

    /// <summary>
    /// The POST actions on one controller type that carry neither attribute, reported as
    /// <c>Type.Method</c>.
    /// </summary>
    private static IEnumerable<string> UndeclaredPostActions(Type controllerType) =>
        controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && !IsCompilerGenerated(m))
            .Where(m => HasAttributeNamed(m, HttpPostAttributeName))
            .Where(m => !HasAttributeNamed(m, IdempotentAttributeName)
                && !HasAttributeNamed(m, NonIdempotentAttributeName))
            .Select(m => $"  - {controllerType.FullName}.{m.Name} must carry [Idempotent] or [NonIdempotent(\"why\")]");

    /// <summary>
    /// Whether the member carries an attribute of the given simple type name, inherited attributes
    /// included, so an override inherits the base action's declaration.
    /// </summary>
    private static bool HasAttributeNamed(MemberInfo member, string attributeTypeName) =>
        member.GetCustomAttributes(inherit: true)
            .Any(a => string.Equals(a.GetType().Name, attributeTypeName, StringComparison.Ordinal));

    /// <summary>Whether the member is compiler-generated (closures, iterator state machines, records' plumbing).</summary>
    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.GetCustomAttributes(inherit: false)
            .Any(a => string.Equals(
                a.GetType().FullName,
                "System.Runtime.CompilerServices.CompilerGeneratedAttribute",
                StringComparison.Ordinal));
}
