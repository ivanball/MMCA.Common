using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Extends <see cref="EntityControllerBase{TEntity, TEntityDTO, TIdentifierType}"/> with Create (POST) and
/// Delete (DELETE) endpoints for aggregate root entities. The Create endpoint is decorated with
/// <see cref="IdempotentAttribute"/> to prevent duplicate resource creation from retried requests.
/// </summary>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned to clients.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TCreateRequest">The request object for entity creation, must implement <see cref="ICreateRequest"/>.</typeparam>
[ApiController]
[Route("[controller]")]
[ApiVersion("1.0")]
public abstract class AggregateRootEntityControllerBase<
    TEntity,
    TEntityDTO,
    TIdentifierType,
    TCreateRequest>(
    IEntityQueryService<TEntity, TEntityDTO, TIdentifierType> queryService,
    ICommandHandler<TCreateRequest, Result<TEntityDTO>> createHandler,
    ICommandHandler<DeleteEntityCommand<TEntity, TIdentifierType>, Result> deleteHandler,
    ILogger<EntityControllerBase<TEntity, TEntityDTO, TIdentifierType>> logger)
    : EntityControllerBase<TEntity, TEntityDTO, TIdentifierType>(queryService, logger)
    , IAggregateRootEntityControllerBase<TEntityDTO, TIdentifierType, TCreateRequest>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
    where TCreateRequest : ICreateRequest
{
    /// <summary>
    /// Gets the create command handler for use in derived controllers that override <see cref="CreateAsync"/>.
    /// </summary>
    protected ICommandHandler<TCreateRequest, Result<TEntityDTO>> CreateHandler { get; } = createHandler;

    /// <summary>
    /// Creates a new entity. The <see cref="IdempotentAttribute"/> ensures that retried requests
    /// with the same <c>Idempotency-Key</c> header return the original response without re-executing
    /// the command. On success, returns 201 Created with a Location header pointing to the new resource.
    /// </summary>
    /// <param name="request">The creation request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created entity DTO with a 201 status, or a Problem Details error response.</returns>
    [HttpPost]
    [Idempotent]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TEntityDTO>> CreateAsync(
        [FromBody, Required] TCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateHandler.HandleAsync(request, cancellationToken);

        // Route name follows the convention "Get{EntityName}ById" established by derived controllers
        return result.IsFailure
            ? HandleFailure(result.Errors)
            : CreatedAtRoute(
                routeName: $"Get{typeof(TEntity).Name}ById",
                routeValues: new { id = result.Value!.Id },
                value: result.Value);
    }

    /// <summary>
    /// Deletes an entity by its identifier. Returns 204 No Content on success.
    /// </summary>
    /// <param name="id">The identifier of the entity to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content on success, or a Problem Details error response.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default)
    {
        var result = await deleteHandler.HandleAsync(new DeleteEntityCommand<TEntity, TIdentifierType>(id), cancellationToken: cancellationToken);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }
}
