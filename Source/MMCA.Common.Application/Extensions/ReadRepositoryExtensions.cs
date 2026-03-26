using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Extensions;

/// <summary>
/// Extension methods for <see cref="IReadRepository{TEntity, TIdentifierType}"/>.
/// </summary>
public static class ReadRepositoryExtensions
{
    /// <summary>
    /// Retrieves a single entity by ID with optional includes and tracking,
    /// returning a <see cref="Result{T}"/> that is a failure with
    /// <see cref="Error.NotFound"/> when the entity does not exist.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
    /// <param name="repository">The repository to query.</param>
    /// <param name="id">The primary key value.</param>
    /// <param name="source">Source name for the error (typically the caller's type name).</param>
    /// <param name="includes">Navigation property names to include.</param>
    /// <param name="asTracking">Whether to track the entity in the change tracker.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result containing the entity, or a failure result with a NotFound error.</returns>
    public static async Task<Result<TEntity>> GetByIdOrFailAsync<TEntity, TIdentifierType>(
        this IReadRepository<TEntity, TIdentifierType> repository,
        TIdentifierType id,
        string source,
        IEnumerable<string>? includes = null,
        bool asTracking = true,
        CancellationToken cancellationToken = default)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var entities = await repository.GetAllAsync(
            includes: includes ?? [],
            where: e => e.Id.Equals(id),
            asTracking: asTracking,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var entity = entities.FirstOrDefault();
        if (entity is null)
        {
            return Result.Failure<TEntity>(
                Error.NotFound.WithSource(source).WithTarget(typeof(TEntity).Name));
        }

        return Result.Success(entity);
    }
}
