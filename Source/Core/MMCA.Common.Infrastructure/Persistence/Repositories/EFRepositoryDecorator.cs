using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// MiniProfiler decorator for <see cref="IRepository{TEntity,TIdentifierType}"/>.
/// Extends <see cref="EFReadRepositoryDecorator{TEntity,TIdentifierType}"/> with profiled
/// write operations (add, update, save). Uses <see cref="ProfilingHelper"/> for timing.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal sealed class EFRepositoryDecorator<TEntity, TIdentifierType>(IRepository<TEntity, TIdentifierType> inner)
    : EFReadRepositoryDecorator<TEntity, TIdentifierType>(inner),
    IRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private const string ClassName = "EFRepository";
    private readonly IRepository<TEntity, TIdentifierType> _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(AddAsync),
            () => _inner.AddAsync(entity, cancellationToken));

    public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(UpdateAsync),
            () => _inner.UpdateAsync(entity, cancellationToken));

    public int Save() =>
        ProfilingHelper.Profile(ClassName, nameof(Save), _inner.Save);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(SaveChangesAsync),
            () => _inner.SaveChangesAsync(cancellationToken));
}
