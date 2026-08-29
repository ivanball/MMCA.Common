namespace MMCA.Common.Domain.Attributes;

/// <summary>
/// Declares a STABLE serialization identity for a domain or integration event, used wherever the
/// event is stored rather than passed in memory: the outbox row that carries it to the bus and the
/// inbox row that dedupes it on the consumer side.
/// <para>
/// Without it, an outbox row records the event's CLR assembly-qualified name, so renaming the class,
/// moving it to another namespace, or moving it to another assembly orphans every row already
/// written under the old name (the processor cannot resolve the type and eventually dead-letters
/// it). With it, the row records this name, which no refactoring changes.
/// </para>
/// </summary>
/// <remarks>
/// Precedence, and why both mechanisms exist:
/// <list type="bullet">
///   <item>This attribute is the PROACTIVE identity. Applied before the rename, rows are written
///   under a name that survives it, so nothing has to be repaired afterwards.</item>
///   <item><c>Outbox:TypeAliases</c> is the RETROACTIVE fix, for rows ALREADY persisted under a name
///   that no longer resolves. Resolution is per stored string, so an alias still wins for those old
///   rows even after the type gains an attribute: the attribute only changes what NEW rows store.</item>
/// </list>
/// Adopting the attribute on an event that already has pending rows therefore means both are in play
/// for a while, which is the intended overlap and not a conflict.
/// <para>
/// The name must be unique across the events a host can resolve, since reverse lookup matches on it.
/// A versioned contract name (<c>"Sales.OrderPlaced.v1"</c>) reads well and leaves room for the
/// upcasting path (ADR-090) when the payload itself changes shape.
/// </para>
/// <code>
/// [EventName("Sales.OrderPlaced.v1")]
/// public sealed record OrderPlaced(int OrderId) : BaseIntegrationEvent;
/// </code>
/// </remarks>
/// <param name="name">The stable serialization identity. Must be non-empty and not whitespace.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class EventNameAttribute(string name) : Attribute
{
    /// <summary>Gets the stable serialization identity stored in place of the CLR type name.</summary>
    public string Name { get; } = Validated(name);

    /// <summary>
    /// Rejects an empty or whitespace name at construction: a blank identity would be stored on
    /// every row of that event and could never be resolved back to a type.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>The validated name.</returns>
    private static string Validated(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
