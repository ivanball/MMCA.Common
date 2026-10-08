using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Crud;
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
#pragma warning disable S6672 // Logger category intentionally matches the base controller; ILogger<T> is not covariant, so the base ctor requires this exact type
    ILogger<EntityControllerBase<TEntity, TEntityDTO, TIdentifierType>> logger)
#pragma warning restore S6672
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
    /// Name of the route that <see cref="CreateAsync"/> links the new resource to, when the derived
    /// controller declares one. Defaults to the <c>Get{EntityName}ById</c> convention; override it
    /// when the controller names its by-id route differently.
    /// </summary>
    protected virtual string GetByIdRouteName => $"Get{EntityName}ById";

    /// <summary>
    /// Creates a new entity. The <see cref="IdempotentAttribute"/> ensures that retried requests
    /// with the same <c>Idempotency-Key</c> header return the original response without re-executing
    /// the command. On success, returns 201 Created with a Location header pointing to the new resource.
    /// </summary>
    /// <param name="request">The creation request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created entity DTO with a 201 status, or a Problem Details error response.</returns>
    /// <remarks>
    /// The Location is resolved here, before the result is returned, and never left to the result
    /// to resolve. A <c>CreatedAtRoute</c> naming a route the derived controller does not declare
    /// throws only when the result EXECUTES, after the entity was saved, so the client received a
    /// 500 for a create that had succeeded. The named route (<see cref="GetByIdRouteName"/>) is used
    /// when it resolves; otherwise the Location is the new id appended to the collection path this
    /// POST was made to, which is where the inherited <c>GET {id}</c> answers.
    /// </remarks>
    [HttpPost]
    [Idempotent]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<TEntityDTO>> CreateAsync(
        [FromBody, Required] TCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateHandler.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return HandleFailure(result.Errors);
        }

        // Link answers an absolute URL; the fallback is host-relative. Each is parsed with its own
        // kind, because RelativeOrAbsolute reads "/x/1" as a file URI on Unix.
        var created = result.Value!;
        var location = Url.Link(GetByIdRouteName, new { id = created.Id }) is { } link
            ? new Uri(link, UriKind.Absolute)
            : new Uri(CollectionMemberPath(created.Id), UriKind.Relative);

        return Created(location, created);
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
        var result = await deleteHandler.HandleAsync(new DeleteEntityCommand<TEntity, TIdentifierType>(id), cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.IsFailure
            ? HandleFailure(result.Errors)
            : NoContent();
    }

    /// <summary>
    /// The path of a collection member: the request's own path (the collection this POST targeted)
    /// with the escaped id appended. Relative to the host, which RFC 9110 permits for Location.
    /// </summary>
    private string CollectionMemberPath(TIdentifierType id)
    {
        var collection = (Request.PathBase + Request.Path).Value?.TrimEnd('/') ?? string.Empty;
        var segment = Uri.EscapeDataString(Convert.ToString(id, CultureInfo.InvariantCulture) ?? string.Empty);
        return string.Concat(collection, "/", segment);
    }
}
