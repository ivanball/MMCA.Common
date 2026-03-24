using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Bridges the Application layer's <see cref="IQueryableExecutor"/> to EF Core extension methods.
/// Guards each call with <see cref="IsEfQuery{T}"/> so the same code path works against
/// both EF Core queryables and plain in-memory <see cref="IQueryable{T}"/> (e.g., in unit tests).
/// </summary>
internal sealed class EFQueryableExecutor : IQueryableExecutor
{
    /// <inheritdoc />
    public IQueryable<T> Include<T>(IQueryable<T> query, string navigationPropertyPath)
        where T : class
        => IsEfQuery(query)
            ? EntityFrameworkQueryableExtensions.Include(query, navigationPropertyPath)
            : query;

    /// <inheritdoc />
    public async Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => IsEfQuery(query)
            ? await EntityFrameworkQueryableExtensions.ToListAsync(query, cancellationToken).ConfigureAwait(false)
            : [.. query];

    /// <inheritdoc />
    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
        => IsEfQuery(query)
            ? EntityFrameworkQueryableExtensions.CountAsync(query, cancellationToken)
            : Task.FromResult(query.Count());

    /// <summary>
    /// Detects EF Core queryables by checking for <see cref="IAsyncEnumerable{T}"/> — EF providers
    /// implement this interface, whereas plain LINQ-to-Objects queryables do not.
    /// </summary>
    private static bool IsEfQuery<T>(IQueryable<T> query) => query is IAsyncEnumerable<T>;
}
