using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// The composed view of every registered <see cref="IEventUpcaster"/>: resolves the terminal (newest)
/// contract for a retired event type and converts an instance to it, walking the whole chain when
/// several versions are registered (V1 to V2 to V3).
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton by <c>AddApplication()</c> and consumed by both delivery paths: the
/// in-process <c>DomainEventDispatcher</c> and the broker-side
/// <c>UpcastingIntegrationEventConsumer&lt;TEvent&gt;</c>. A host that registers no upcaster gets an
/// empty registry whose methods are identity operations (ADR-090).
/// </para>
/// <para>
/// The implementation validates the registration graph in its constructor (duplicate source, a
/// source mapped onto itself, or a cycle throw <see cref="InvalidOperationException"/> naming the
/// offenders), and an internal startup validator resolves it at host start so a misconfiguration
/// fails the host rather than the first message.
/// </para>
/// </remarks>
public interface IEventUpcasterRegistry
{
    /// <summary>
    /// Returns whether any upcaster claims <paramref name="eventType"/> as its source contract.
    /// </summary>
    /// <param name="eventType">The event type to probe.</param>
    /// <returns><see langword="true"/> when a registered upcaster reads this type.</returns>
    bool HasUpcasterFor(Type eventType);

    /// <summary>
    /// Resolves the terminal (newest) contract <paramref name="eventType"/> upcasts to, following the
    /// whole chain. Returns <paramref name="eventType"/> itself when no upcaster claims it.
    /// </summary>
    /// <param name="eventType">The event type to resolve.</param>
    /// <returns>The terminal event type.</returns>
    Type ResolveTerminalType(Type eventType);

    /// <summary>
    /// Upcasts <paramref name="integrationEvent"/> to its terminal contract, applying every hop in
    /// the chain and preserving the envelope (<c>MessageId</c>, <c>DateOccurred</c>) at each one.
    /// Returns the original instance when no upcaster claims its type.
    /// </summary>
    /// <param name="integrationEvent">The event to upcast.</param>
    /// <returns>The terminal-contract instance.</returns>
    IIntegrationEvent UpcastToTerminal(IIntegrationEvent integrationEvent);
}
