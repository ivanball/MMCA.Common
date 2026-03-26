using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;
using StackExchange.Profiling;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// Decorator that wraps every <see cref="IReadRepository{TEntity,TIdentifierType}"/> operation
/// in a MiniProfiler timing step for performance visibility in development.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal class EFReadRepositoryDecorator<TEntity, TIdentifierType>(IReadRepository<TEntity, TIdentifierType> inner)
    : IReadRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private readonly IReadRepository<TEntity, TIdentifierType> _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<IReadOnlyCollection<TEntity>> GetAllAsync(
        IEnumerable<string> includes,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        Expression<Func<TEntity, TEntity>>? select = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(GetAllAsync),
            () => _inner.GetAllAsync(includes, where, orderBy, select, asTracking, ignoreQueryFilters, cancellationToken));

    public Task<IReadOnlyCollection<TResult>> GetProjectedAsync<TResult>(
        Expression<Func<TEntity, TResult>> select,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(GetProjectedAsync),
            () => _inner.GetProjectedAsync(select, where, asTracking, cancellationToken));

    public Task<IReadOnlyCollection<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(GetAllForLookupAsync),
            () => _inner.GetAllForLookupAsync(nameProperty, where, asTracking, cancellationToken));

    public Task<TEntity?> GetByIdAsync(TIdentifierType id, CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(GetByIdAsync),
            () => _inner.GetByIdAsync(id, cancellationToken));

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(CountAsync),
            () => _inner.CountAsync(cancellationToken));

    public Task<int> CountAsync(Expression<Func<TEntity, bool>> where, CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(CountAsync),
            () => _inner.CountAsync(where, cancellationToken));

    public Task<bool> ExistsAsync(TIdentifierType id, bool ignoreQueryFilters = false, CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(ExistsAsync),
            () => _inner.ExistsAsync(id, ignoreQueryFilters, cancellationToken));

    public Task<bool> ExistsAsync(Expression<Func<TEntity, bool>> where, bool ignoreQueryFilters = false, CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(ExistsAsync),
            () => _inner.ExistsAsync(where, ignoreQueryFilters, cancellationToken));

    public IQueryable<TEntity> Table => _inner.Table;
    public IQueryable<TEntity> TableNoTracking => _inner.TableNoTracking;
    public IQueryable<TEntity> TableNoTrackingSingleQuery => _inner.TableNoTrackingSingleQuery;
    public IQueryable<TEntity> TableNoTrackingSplitQuery => _inner.TableNoTrackingSplitQuery;

    private static async Task<T> ProfileAsync<T>(string methodName, Func<Task<T>> func)
    {
        using var step = MiniProfiler.Current?.Step($"MMCA.Common.Infrastructure.EFReadRepository: {methodName}");
        return await func().ConfigureAwait(false);
    }
}
