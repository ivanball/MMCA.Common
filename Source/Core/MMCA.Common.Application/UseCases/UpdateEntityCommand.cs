using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Generic update command for any aggregate root entity: the entity's primary key, the caller's
/// last-observed concurrency token, and the update request the aggregate has to accept. The
/// <typeparamref name="TEntity"/> type parameter distinguishes update handlers for different entity
/// types that share the same identifier type, and supplies the default cache prefix evicted after a
/// successful update.
/// </summary>
/// <remarks>
/// <para>
/// The command carries the request rather than flattening it, so it implements
/// <see cref="ICommandWithRequest{TRequest}"/> and the framework's validator bridge registers a
/// <c>CommandRequestValidator</c> for it automatically: a module only writes
/// <c>IValidator&lt;TUpdateRequest&gt;</c> and the command is validated before the transaction opens.
/// </para>
/// <para>
/// The mutation itself is not here. It lives on the aggregate, reached through the module's
/// <c>IEntityUpdateApplier</c>, which is what keeps one generic command usable for every entity
/// without the command knowing a single field name.
/// </para>
/// <para>
/// <b>The record is not sealed, on purpose.</b> A real update often carries state beside the request:
/// an id taken from the route rather than the body (the child row a PATCH addresses), a flag the
/// server decided rather than the caller (whether the caller holds an organizer role), a second
/// concurrency token for a child. Deriving a positional record from this one adds those properties
/// while inheriting <c>Id</c>, <c>Request</c>, <c>RowVersion</c>, the
/// <see cref="ICommandWithRequest{TRequest}"/> validator bridge and the cache prefix, and
/// <see cref="UpdateEntityCommandHandler{TCommand, TEntity, TEntityDTO, TIdentifierType, TUpdateRequest}"/>
/// serves it with an applier that sees the whole command. A derived command declared in a module
/// assembly is picked up by the module scan's validator bridge like any other command.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type to update.</typeparam>
/// <typeparam name="TUpdateRequest">The update request the applier consumes.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <param name="Id">The primary key of the entity to update.</param>
/// <param name="Request">The update request payload.</param>
/// <param name="RowVersion">
/// The caller's last-observed concurrency token (ADR-035), or <see langword="null"/> to skip the
/// stale-view check. A body-less or legacy caller simply passes nothing.
/// </param>
public record UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>(
    TIdentifierType Id,
    TUpdateRequest Request,
    byte[]? RowVersion = null)
    : ICommandWithRequest<TUpdateRequest>, ICacheInvalidating
    where TIdentifierType : notnull
{
    /// <summary>
    /// Gets the cache key prefix evicted after a successful update. Defaults to the
    /// aggregate-prefix convention every consumer already keys its cached reads under
    /// (<c>{entity full name}:</c>), because the generic controller constructs this command
    /// itself and cannot supply one. Set it to an empty string to opt out of invalidation.
    /// </summary>
    /// <remarks>
    /// A derived command inherits the same default and the same opt-out, and can narrow it by
    /// initializing the property to a per-verb prefix.
    /// </remarks>
    public string CachePrefix { get; init; } = typeof(TEntity).FullName + ":";
}

/// <summary>
/// The verb-discriminated generic update command: the same payload as
/// <see cref="UpdateEntityCommand{TEntity, TUpdateRequest, TIdentifierType}"/> plus the applier type
/// that has to serve it, so two verbs over one request DTO stay two distinct commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem it solves.</b> The generic write side keys the handler and its applier on
/// (entity, request, identifier). An aggregate with two verbs that take the same request shape (an
/// inventory item increased or decreased by the same <c>Quantity</c> payload) therefore cannot close
/// the three-parameter command twice: both verbs would resolve the same handler and the same
/// applier. <typeparamref name="TApplier"/> is a phantom type parameter that discriminates them:
/// each verb closes this command over its own applier type, and
/// <see cref="UpdateEntityHandler{TEntity, TEntityDTO, TIdentifierType, TUpdateRequest, TApplier}"/>
/// resolves that exact applier from the container.
/// </para>
/// <para>
/// The wire shape is unchanged: the route and the request DTO stay what they were, and only the
/// command and applier typing behind the controller action differ per verb. Registration is one
/// <c>AddEntityUpdateVerb</c> call per verb, which also bridges the command to
/// <c>IValidator&lt;TUpdateRequest&gt;</c>.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type to update.</typeparam>
/// <typeparam name="TUpdateRequest">The update request the applier consumes.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TApplier">
/// The applier that serves this verb. Present to discriminate the command, never resolved from the
/// command itself.
/// </typeparam>
/// <param name="Id">The primary key of the entity to update.</param>
/// <param name="Request">The update request payload.</param>
/// <param name="RowVersion">
/// The caller's last-observed concurrency token (ADR-035), or <see langword="null"/> to skip the
/// stale-view check.
/// </param>
public record UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType, TApplier>(
    TIdentifierType Id,
    TUpdateRequest Request,
    byte[]? RowVersion = null)
    : UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>(Id, Request, RowVersion)
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TApplier : class, IEntityUpdateApplier<TEntity, TUpdateRequest, TIdentifierType>
{
    /// <summary>
    /// Gets the applier type this verb is discriminated by. The command never resolves the applier
    /// itself (that is the handler's job through the container); the property makes the discriminator
    /// readable in a log line or a test assertion instead of only in the type name.
    /// </summary>
    public Type ApplierType { get; } = typeof(TApplier);
}
