using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.API.Controllers;

/// <summary>
/// Extends <see cref="IEntityControllerBase{TEntityDTO, TIdentifierType}"/> with Create and Delete
/// operations for aggregate root entities.
/// </summary>
/// <typeparam name="TEntityDTO">The DTO type returned to clients.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
/// <typeparam name="TCreateRequest">The creation request type.</typeparam>
public interface IAggregateRootEntityControllerBase<
    TEntityDTO,
    TIdentifierType,
    TCreateRequest>
    : IEntityControllerBase<TEntityDTO, TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
    where TCreateRequest : ICreateRequest
{
    /// <summary>Creates a new entity from the provided request.</summary>
    /// <param name="request">The creation request payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created entity DTO with a 201 status.</returns>
    Task<ActionResult<TEntityDTO>> CreateAsync(
        [Required] TCreateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an entity by its identifier.</summary>
    /// <param name="id">The identifier of the entity to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content on success.</returns>
    Task<ActionResult> DeleteAsync(
        TIdentifierType id,
        CancellationToken cancellationToken = default);
}
