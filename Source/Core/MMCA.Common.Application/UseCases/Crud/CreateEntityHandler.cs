using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.UseCases.Crud;

/// <summary>
/// The ready-made create handler: <see cref="CreateEntityHandlerBase{TCreateRequest, TEntity, TIdentifierType, TEntityDTO}"/>
/// with none of its hooks overridden. Every hook on the base is virtual, so an aggregate whose create
/// needs nothing beyond "map the request through the factory, add, save, return the DTO" needs no
/// subclass at all: closing this type over the aggregate's four types is the whole registration.
/// </summary>
/// <remarks>
/// Reach for the base class instead the moment a create needs something of its own: a pre-map step
/// that resolves an app-assigned key or runs a cross-aggregate check (<c>PrepareAsync</c>), a
/// module-specific log message (<c>LogCreated</c>), a post-commit publish (<c>OnCreatedAsync</c>), or
/// the manual-id retry loop. Those are exactly the reasons the base exists; this type is the floor
/// beneath them, and it is sealed because the base, not this, is the extension point.
/// </remarks>
/// <typeparam name="TCreateRequest">The create request DTO, which is also the command.</typeparam>
/// <typeparam name="TEntity">The aggregate root being created.</typeparam>
/// <typeparam name="TIdentifierType">The aggregate's primary key type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO returned on success.</typeparam>
/// <param name="unitOfWork">The ambient unit of work.</param>
/// <param name="requestMapper">The module's request-to-entity mapper.</param>
/// <param name="dtoMapper">The module's entity-to-DTO mapper.</param>
public sealed class CreateEntityHandler<TCreateRequest, TEntity, TIdentifierType, TEntityDTO>(
    IUnitOfWork unitOfWork,
    IEntityRequestMapper<TEntity, TCreateRequest, TIdentifierType> requestMapper,
    IEntityDTOMapper<TEntity, TEntityDTO, TIdentifierType> dtoMapper)
    : CreateEntityHandlerBase<TCreateRequest, TEntity, TIdentifierType, TEntityDTO>(unitOfWork, requestMapper, dtoMapper)
    where TCreateRequest : ICreateRequest
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
    where TEntityDTO : IBaseDTO<TIdentifierType>;
