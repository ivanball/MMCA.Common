using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Testing.Support;

/// <summary>
/// An <see cref="IQueryableExecutor"/> that runs the queryable for real with LINQ to Objects. Use it
/// to drive the real query pipeline (filtering, sorting, paging, projection) over an in-memory
/// sequence, where a mocked executor returning a canned list could not show the order or the page the
/// pipeline actually produced.
/// </summary>
/// <remarks>
/// <see cref="Include{T}"/> and <see cref="AsSplitQuery{T}"/> return the query unchanged: navigations
/// on in-memory objects are already populated (or not) by the test, and split queries are an EF
/// round-trip concern with no LINQ to Objects meaning.
/// </remarks>
public sealed class InMemoryQueryableExecutor : IQueryableExecutor
{
    /// <inheritdoc />
    public IQueryable<T> Include<T>(IQueryable<T> query, string navigationPropertyPath)
        where T : class
        => query;

    /// <inheritdoc />
    public IQueryable<T> AsSplitQuery<T>(IQueryable<T> query)
        where T : class
        => query;

    /// <inheritdoc />
    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => Task.FromResult(query.ToList());

    /// <inheritdoc />
    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => Task.FromResult(query.Count());
}
