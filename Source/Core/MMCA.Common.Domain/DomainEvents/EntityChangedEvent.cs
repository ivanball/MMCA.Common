using MMCA.Common.Domain.Enums;

namespace MMCA.Common.Domain.DomainEvents;

/// <summary>
/// Standard base record for CRUD lifecycle domain events. Consolidates the
/// <c>{Entity}Created</c>, <c>{Entity}Changed</c>, and <c>{Entity}Deleted</c>
/// pattern into a single event type per entity using <see cref="DomainEntityState"/>.
/// <para>
/// <b>Usage:</b> Derive one record per entity. Raise it with <see cref="DomainEntityState.Added"/>
/// from factory methods, <see cref="DomainEntityState.Updated"/> from mutation methods, and
/// <see cref="DomainEntityState.Deleted"/> from <c>Delete()</c>. Handlers filter on
/// <see cref="State"/> to decide which transitions they care about.
/// </para>
/// <para>
/// <b>Business-specific events</b> (e.g., <c>OrderPaid</c>, <c>ShoppingCartCheckedOut</c>)
/// that represent state machine transitions with unique payloads should continue to inherit
/// directly from <see cref="BaseDomainEvent"/> — this base is only for generic CRUD lifecycle.
/// </para>
/// </summary>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <param name="State">The lifecycle state change that triggered this event.</param>
/// <param name="EntityId">The identifier of the affected entity.</param>
public abstract record EntityChangedEvent<TIdentifierType>(
    DomainEntityState State,
    TIdentifierType EntityId) : BaseDomainEvent
    where TIdentifierType : notnull;
