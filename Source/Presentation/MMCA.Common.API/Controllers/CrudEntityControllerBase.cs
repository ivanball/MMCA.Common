using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Extends <see cref="AggregateRootEntityControllerBase{TEntity, TEntityDTO, TIdentifierType, TCreateRequest}"/>
/// with the Update (PUT) endpoint, completing the generic write side: read, create, update, delete
/// with no per-entity action bodies at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate base and not one more type parameter on the aggregate-root base.</b> The update
/// endpoint needs a fifth type, the update request, and adding it to the shipped four-parameter base
/// would change that type's generic arity: every controller in every consuming app would stop
/// compiling on the next version bump. This base is additive instead. A controller that offers only
/// create and delete keeps inheriting the four-parameter base; one that also offers update inherits
/// this and gains the action.
/// </para>
/// <para>
/// <b>Concurrency.</b> The update is conditional (ADR-035): <see cref="SupportsIfMatchAttribute"/>
/// decodes the caller's <c>If-Match</c> header into the command's <c>RowVersion</c>, refuses a
/// request that states no precondition with 428, and answers a failed one with 412. The request body
/// carries no token. On success the refreshed token is emitted as a weak <c>ETag</c> through the
/// inherited <c>SetConcurrencyETag</c>, so the client can condition its next write without re-reading
/// the resource.
/// </para>
/// <para>
/// <b>Cache eviction.</b> The command's default <c>CachePrefix</c> evicts the aggregate's cached
/// reads through the caching decorator. An app that also uses ASP.NET output caching overrides
/// <see cref="UpdateAsync"/>, awaits <c>base.UpdateAsync</c>, and evicts its own output-cache tags on
/// success, exactly as it does for the inherited create and delete actions.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned to clients.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TCreateRequest">The request object for entity creation, must implement <see cref="ICreateRequest"/>.</typeparam>
/// <typeparam name="TUpdateRequest">The request object for entity update.</typeparam>
[ApiController]
[Route("[controller]")]
[ApiVersion("1.0")]
public abstract class CrudEntityControllerBase<
    TEntity,
    TEntityDTO,
    TIdentifierType,
    TCreateRequest,
    TUpdateRequest>(
    IEntityQueryService<TEntity, TEntityDTO, TIdentifierType> queryService,
    ICommandHandler<TCreateRequest, Result<TEntityDTO>> createHandler,
    ICommandHandler<UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>, Result<TEntityDTO>> updateHandler,
    ICommandHandler<DeleteEntityCommand<TEntity, TIdentifierType>, Result> deleteHandler,
#pragma warning disable S6672 // Logger category intentionally matches the base controller; ILogger<T> is not covariant, so the base ctor requires this exact type
    ILogger<EntityControllerBase<TEntity, TEntityDTO, TIdentifierType>> logger)
#pragma warning restore S6672
    : AggregateRootEntityControllerBase<TEntity, TEntityDTO, TIdentifierType, TCreateRequest>(
        queryService, createHandler, deleteHandler, logger)
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
    where TCreateRequest : ICreateRequest
{
    /// <summary>
    /// Gets the update command handler, for a derived controller that overrides
    /// <see cref="UpdateAsync"/> entirely rather than wrapping it.
    /// </summary>
    protected ICommandHandler<UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>, Result<TEntityDTO>> UpdateHandler { get; } = updateHandler;

    /// <summary>
    /// Updates an existing entity. Returns 200 OK with the refreshed DTO and its <c>ETag</c>, so the
    /// caller re-renders from the response instead of issuing a follow-up read.
    /// </summary>
    /// <param name="id">The identifier of the entity to update.</param>
    /// <param name="request">The update request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity DTO with a 200 status, or a Problem Details error response.</returns>
    [HttpPut("{id}")]
    [Idempotent]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TEntityDTO>> UpdateAsync(
        TIdentifierType id,
        [FromBody, Required] TUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var rowVersion = SupportsIfMatchAttribute.RequiredToken(HttpContext);

        var result = await UpdateHandler.HandleAsync(
            new UpdateEntityCommand<TEntity, TUpdateRequest, TIdentifierType>(id, request, rowVersion),
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
            return HandleFailure(result.Errors);

        SetConcurrencyETag(result.Value);
        return Ok(result.Value);
    }
}
