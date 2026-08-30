using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Applies an incoming update <b>command</b> to an already-loaded aggregate. The command-aware twin
/// of <c>IEntityUpdateApplier</c>: it receives the whole command rather than only its request, so an
/// update that also depends on state the request does not carry can still run on the generic path.
/// Implementations are auto-registered via Scrutor assembly scanning, exactly like the request-only
/// applier.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the command and not just the request.</b> A request DTO is the body a caller sent. Plenty
/// of updates depend on more than the body: an identifier taken from the route (the child row a
/// nested PUT addresses), a decision the server made rather than the caller (whether the caller holds
/// a role that unlocks a field), a second concurrency token for a child row. Those belong on the
/// command, not smuggled into the request DTO where a caller could set them, so the applier has to
/// see the command.
/// </para>
/// <para>
/// It lives beside <see cref="UpdateEntityCommand{TEntity, TUpdateRequest, TIdentifierType}"/> rather
/// than with the request-only applier because it is bound to that command: the
/// <typeparamref name="TCommand"/> constraint is what gives an implementation the inherited
/// <c>Id</c>, <c>Request</c> and <c>RowVersion</c> alongside its own properties.
/// </para>
/// <para>
/// Like the request-only applier it answers with a bare <see cref="Result"/>: the instance handed in
/// is the tracked one, so a successful apply has already mutated it in place and a failure must
/// leave it untouched.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The domain entity type being updated.</typeparam>
/// <typeparam name="TUpdateRequest">The incoming update request DTO the command carries.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TCommand">The command type, derived from the generic update command.</typeparam>
public interface IEntityUpdateCommandApplier<TEntity, TUpdateRequest, TIdentifierType, in TCommand>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TCommand : UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>
{
    /// <summary>
    /// Applies the command to the loaded aggregate, returning the aggregate's own
    /// <see cref="Result"/> so a refused invariant stops the write before it is saved.
    /// </summary>
    /// <param name="entity">The loaded, tracked aggregate to mutate.</param>
    /// <param name="command">The command being handled, with everything it carries beside the request.</param>
    /// <param name="context">
    /// The run's mutation context: write a value derived here that the handler's post-save hooks or
    /// its result need, or call <see cref="MutationContext.SkipSave"/> to finish an already-satisfied
    /// command without writing.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success when the aggregate accepted every change, otherwise the refusal.</returns>
    Task<Result> ApplyAsync(
        TEntity entity,
        TCommand command,
        MutationContext context,
        CancellationToken cancellationToken = default);
}
