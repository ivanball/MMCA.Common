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
/// The attribute is the one type-resolution mechanism, and it works by being applied BEFORE the
/// refactoring: rows written from that point on carry a name the refactoring does not touch. It
/// changes only what NEW rows store, so rows already persisted under a CLR name keep resolving by
/// that name and stop resolving if it goes away. Applying it while an outbox holds pending rows is
/// therefore a two-step move: drain the pending rows first, then rename.
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
