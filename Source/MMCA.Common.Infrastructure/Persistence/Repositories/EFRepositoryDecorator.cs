using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using StackExchange.Profiling;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// MiniProfiler decorator for <see cref="IRepository{TEntity,TIdentifierType}"/>.
/// Extends <see cref="EFReadRepositoryDecorator{TEntity,TIdentifierType}"/> with profiled
/// write operations (add, update, save). Uses the same profiling approach as the read decorator.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
internal sealed class EFRepositoryDecorator<TEntity, TIdentifierType>(IRepository<TEntity, TIdentifierType> inner)
    : EFReadRepositoryDecorator<TEntity, TIdentifierType>(inner),
    IRepository<TEntity, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private readonly IRepository<TEntity, TIdentifierType> _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(AddAsync),
            () => _inner.AddAsync(entity, cancellationToken));

    public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(UpdateAsync),
            () => _inner.UpdateAsync(entity, cancellationToken));

    public int Save() =>
        ProfileAsync(nameof(Save),
            _inner.Save);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        ProfileAsync(nameof(SaveChangesAsync),
            () => _inner.SaveChangesAsync(cancellationToken));

    private static Timing? BeginProfile(string methodName) =>
        MiniProfiler.Current?.Step($"MMCA.Common.Infrastructure.EFRepository: {methodName}");

    private static int ProfileAsync(string methodName, Func<int> func)
    {
        using var step = BeginProfile(methodName);
        return func();
    }

    private static async Task ProfileAsync(string methodName, Func<Task> func)
    {
        using var step = BeginProfile(methodName);
        await func().ConfigureAwait(false);
    }

    private static async Task<T> ProfileAsync<T>(string methodName, Func<Task<T>> func)
    {
        using var step = BeginProfile(methodName);
        return await func().ConfigureAwait(false);
    }
}
