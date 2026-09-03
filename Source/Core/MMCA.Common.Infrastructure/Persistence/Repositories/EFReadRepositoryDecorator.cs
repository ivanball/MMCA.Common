using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// Decorator that wraps every <see cref="IReadRepository{TEntity,TIdentifierType}"/> operation
/// in a MiniProfiler timing step for performance visibility in development.
/// Uses <see cref="ProfilingHelper"/> for timing.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal class EFReadRepositoryDecorator<TEntity, TIdentifierType>(IReadRepository<TEntity, TIdentifierType> inner)
    : IReadRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private const string ClassName = "EFReadRepository";
    private readonly IReadRepository<TEntity, TIdentifierType> _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task<IReadOnlyCollection<TEntity>> GetAllAsync(
        IEnumerable<string> includes,
        Expression<Func<TEntity, bool>>? where = null,
        Expression<Func<TEntity, string>>? orderBy = null,
        Expression<Func<TEntity, TEntity>>? select = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetAllAsync),
            () => _inner.GetAllAsync(includes, where, orderBy, select, asTracking, ignoreQueryFilters, cancellationToken));

    public Task<IReadOnlyCollection<TResult>> GetProjectedAsync<TResult>(
        Expression<Func<TEntity, TResult>> select,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetProjectedAsync),
            () => _inner.GetProjectedAsync(select, where, asTracking, ignoreQueryFilters, cancellationToken));

    public Task<TEntity?> FirstOrDefaultAsync(
        Expression<Func<TEntity, bool>> where,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(FirstOrDefaultAsync),
            () => _inner.FirstOrDefaultAsync(where, includes, asTracking, ignoreQueryFilters, cancellationToken));

    public Task<TEntity?> FirstOrDefaultAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(FirstOrDefaultAsync),
            () => _inner.FirstOrDefaultAsync(specification, cancellationToken));

    public Task<IReadOnlyDictionary<TKey, int>> CountByAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, bool>>? where = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(CountByAsync),
            () => _inner.CountByAsync(keySelector, where, cancellationToken));

    public Task<IReadOnlyDictionary<TKey, decimal>> SumByAsync<TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, decimal>> sumSelector,
        Expression<Func<TEntity, bool>>? where = null,
        CancellationToken cancellationToken = default)
        where TKey : notnull =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(SumByAsync),
            () => _inner.SumByAsync(keySelector, sumSelector, where, cancellationToken));

    public Task<(IReadOnlyCollection<TEntity> Active, IReadOnlyCollection<TEntity> SoftDeleted)> FindIncludingDeletedAsync(
        Expression<Func<TEntity, bool>> where,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(FindIncludingDeletedAsync),
            () => _inner.FindIncludingDeletedAsync(where, includes, asTracking, cancellationToken));

    public Task<IReadOnlyCollection<BaseLookup<TIdentifierType>>> GetAllForLookupAsync(
        string nameProperty,
        Expression<Func<TEntity, bool>>? where = null,
        bool asTracking = false,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetAllForLookupAsync),
            () => _inner.GetAllForLookupAsync(nameProperty, where, asTracking, cancellationToken));

    public Task<IReadOnlyCollection<TEntity>> GetByIdsAsync(
        IEnumerable<TIdentifierType> ids,
        IEnumerable<string>? includes = null,
        bool asTracking = false,
        bool ignoreQueryFilters = false,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetByIdsAsync),
            () => _inner.GetByIdsAsync(ids, includes, asTracking, ignoreQueryFilters, cancellationToken));

    public Task<TEntity?> GetByIdAsync(TIdentifierType id, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetByIdAsync),
            () => _inner.GetByIdAsync(id, cancellationToken));

    public Task<TEntity?> GetByIdAsync(TIdentifierType id, IEnumerable<string> includes, bool asTracking = false, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetByIdAsync),
            () => _inner.GetByIdAsync(id, includes, asTracking, cancellationToken));

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(CountAsync),
            () => _inner.CountAsync(cancellationToken));

    public Task<int> CountAsync(Expression<Func<TEntity, bool>> where, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(CountAsync),
            () => _inner.CountAsync(where, cancellationToken));

    public Task<int> CountAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(CountAsync),
            () => _inner.CountAsync(specification, cancellationToken));

    public Task<bool> ExistsAsync(TIdentifierType id, bool ignoreQueryFilters = false, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(ExistsAsync),
            () => _inner.ExistsAsync(id, ignoreQueryFilters, cancellationToken));

    public Task<bool> ExistsAsync(Expression<Func<TEntity, bool>> where, bool ignoreQueryFilters = false, CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(ExistsAsync),
            () => _inner.ExistsAsync(where, ignoreQueryFilters, cancellationToken));

    public Task<IReadOnlyCollection<TEntity>> ListAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(ListAsync),
            () => _inner.ListAsync(specification, cancellationToken));

    public Task<IReadOnlyCollection<TResult>> ListAsync<TResult>(
        ISpecification<TEntity, TIdentifierType> specification,
        Expression<Func<TEntity, TResult>> select,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(ListAsync),
            () => _inner.ListAsync(specification, select, cancellationToken));

    public Task<bool> AnyAsync(
        ISpecification<TEntity, TIdentifierType> specification,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(AnyAsync),
            () => _inner.AnyAsync(specification, cancellationToken));

    public Task<Result<KeysetCollectionResult<TEntity>>> GetPageByCursorAsync(
        KeysetPageRequest request,
        ISpecification<TEntity, TIdentifierType>? specification = null,
        CancellationToken cancellationToken = default) =>
        ProfilingHelper.ProfileAsync(ClassName, nameof(GetPageByCursorAsync),
            () => _inner.GetPageByCursorAsync(request, specification, cancellationToken));

    public IQueryable<TEntity> Table => _inner.Table;
    public IQueryable<TEntity> TableNoTracking => _inner.TableNoTracking;
    public IQueryable<TEntity> TableNoTrackingSingleQuery => _inner.TableNoTrackingSingleQuery;
    public IQueryable<TEntity> TableNoTrackingSplitQuery => _inner.TableNoTrackingSplitQuery;
}
