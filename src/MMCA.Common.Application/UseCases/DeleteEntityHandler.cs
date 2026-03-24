using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Generic delete handler that works for any aggregate root entity. Retrieves the entity
/// by ID, invokes its <c>Delete()</c> method (which may enforce business rules and raise
/// domain events), and persists the change if successful.
/// </summary>
/// <typeparam name="TEntity">The aggregate root entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public class DeleteEntityHandler<TEntity, TIdentifierType>(
    IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteEntityCommand<TEntity, TIdentifierType>, Result>
    where TEntity : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        DeleteEntityCommand<TEntity, TIdentifierType> command,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<TEntity, TIdentifierType>();
        var entity = await repository.GetByIdAsync(command.Id, cancellationToken);
        if (entity is null)
            return Result.Failure(Error.NotFound.WithSource(nameof(DeleteEntityHandler<,>)).WithTarget(typeof(TEntity).Name));

        var result = entity.Delete();
        if (result.IsSuccess)
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }
}
