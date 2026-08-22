using System.Diagnostics.CodeAnalysis;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Non-generic view of an event upcaster: converts one retired integration-event contract into its
/// successor. The framework resolves every registration through this interface (the registry indexes
/// by <see cref="SourceType"/> and walks chains by <see cref="TargetType"/>); application code
/// implements the typed <see cref="IEventUpcaster{TSource, TTarget}"/> instead, which supplies these
/// members as default interface implementations.
/// </summary>
/// <remarks>
/// <para>
/// ADR-010 makes a breaking event-shape change (a renamed, removed or retyped field) a NEW event
/// type plus a consumer-side upcaster, never a silent reshape of an existing type. ADR-090 is the
/// registration extension point: <c>services.AddEventUpcaster&lt;TOld, TNew, TUpcaster&gt;()</c> for
/// the in-process path, plus <c>x.RegisterUpcastedIntegrationEventConsumer&lt;TOld&gt;()</c> when the
/// retired contract still arrives over a broker.
/// </para>
/// <para>
/// <b>Map payload fields only.</b> The framework preserves the envelope: after every hop the registry
/// stamps <c>MessageId</c> and <c>DateOccurred</c> from the pre-hop instance onto the upcasted one, so
/// inbox deduplication keeps working on the id the producer published. An upcaster that copies them
/// itself is harmless (the stamp is idempotent), and one that forgets is still correct.
/// </para>
/// </remarks>
public interface IEventUpcaster
{
    /// <summary>Gets the retired event contract this upcaster reads.</summary>
    Type SourceType { get; }

    /// <summary>Gets the successor event contract this upcaster produces.</summary>
    Type TargetType { get; }

    /// <summary>
    /// Converts an instance of <see cref="SourceType"/> into an instance of <see cref="TargetType"/>.
    /// </summary>
    /// <param name="integrationEvent">The event to convert. Must be an instance of <see cref="SourceType"/>.</param>
    /// <returns>The upcasted event.</returns>
    IIntegrationEvent Upcast(IIntegrationEvent integrationEvent);
}

/// <summary>
/// Converts the retired integration-event contract <typeparamref name="TSource"/> into its successor
/// <typeparamref name="TTarget"/>, so handlers are written once against the newest contract while
/// older messages (queued at the broker, or sitting unprocessed in an outbox written before the
/// upgrade) keep being delivered.
/// </summary>
/// <typeparam name="TSource">The retired event contract.</typeparam>
/// <typeparam name="TTarget">The successor event contract. Must declare a higher <c>SchemaVersion</c>.</typeparam>
/// <remarks>
/// <para>
/// Implementations are pure functions and are registered as singletons:
/// <c>services.AddEventUpcaster&lt;ProductVariantChanged, ProductVariantChangedV2, ProductVariantChangedUpcaster&gt;()</c>.
/// Chains compose: registering V1 to V2 and V2 to V3 delivers a V1 message to the V3 handler.
/// </para>
/// <para>
/// Map payload fields only. The registry preserves <c>MessageId</c> and <c>DateOccurred</c> across
/// every hop, so consumer-side inbox deduplication stays keyed on the id the producer published.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1033:Interface methods should be callable by child types",
    Justification = "A default interface implementation of an inherited member can only be written as an explicit implementation; there is no non-explicit form. Implementers supply the typed Upcast overload and the framework consumes SourceType/TargetType through the IEventUpcaster interface, which is how the registry indexes them.")]
public interface IEventUpcaster<in TSource, out TTarget> : IEventUpcaster
    where TSource : class, IIntegrationEvent
    where TTarget : class, IIntegrationEvent
{
    /// <inheritdoc />
    Type IEventUpcaster.SourceType => typeof(TSource);

    /// <inheritdoc />
    Type IEventUpcaster.TargetType => typeof(TTarget);

    /// <summary>
    /// Converts <paramref name="integrationEvent"/> into the successor contract.
    /// </summary>
    /// <param name="integrationEvent">The retired-contract event to convert.</param>
    /// <returns>The upcasted event. Never <see langword="null"/>.</returns>
    TTarget Upcast(TSource integrationEvent);

    /// <inheritdoc />
    IIntegrationEvent IEventUpcaster.Upcast(IIntegrationEvent integrationEvent) =>
        Upcast((TSource)integrationEvent);
}
