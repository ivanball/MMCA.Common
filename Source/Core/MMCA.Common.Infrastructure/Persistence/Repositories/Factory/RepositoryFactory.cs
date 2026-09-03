using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Repositories.Factory;

/// <summary>
/// Creates repository instances, conditionally wrapping them in MiniProfiler decorators
/// when profiling is enabled in <see cref="ApplicationSettings"/>.
/// </summary>
public sealed class RepositoryFactory(IServiceProvider serviceProvider, IOptions<ApplicationSettings> applicationSettings) : IRepositoryFactory
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ApplicationSettings _applicationSettings = applicationSettings.Value;

    /// <inheritdoc />
    /// <remarks>
    /// When <see cref="ApplicationSettings.UseMiniProfiler"/> is <see langword="true"/>, the base
    /// <see cref="EFRepository{TEntity,TIdentifierType}"/> is wrapped in an
    /// <see cref="EFRepositoryDecorator{TEntity,TIdentifierType}"/> that records timing data.
    /// </remarks>
    public IRepository<TEntity, TIdentifierType> Create<TEntity, TIdentifierType>(
        DbContext dbContext)
        where TEntity : AuditableAggregateRootEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var repositoryInstance = (IRepository<TEntity, TIdentifierType>)Factory(
            typeof(EFRepository<TEntity, TIdentifierType>), DbContextArg)(_serviceProvider, [dbContext]);

        if (_applicationSettings.UseMiniProfiler)
        {
            repositoryInstance = (IRepository<TEntity, TIdentifierType>)Factory(
                typeof(EFRepositoryDecorator<TEntity, TIdentifierType>),
                [typeof(IRepository<TEntity, TIdentifierType>)])(_serviceProvider, [repositoryInstance]);
        }

        return repositoryInstance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// When <see cref="ApplicationSettings.UseMiniProfiler"/> is <see langword="true"/>, the base
    /// <see cref="EFReadRepository{TEntity,TIdentifierType}"/> is wrapped in an
    /// <see cref="EFReadRepositoryDecorator{TEntity,TIdentifierType}"/> that records timing data.
    /// </remarks>
    public IReadRepository<TEntity, TIdentifierType> CreateReadOnly<TEntity, TIdentifierType>(
        DbContext dbContext)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var repositoryInstance = (IReadRepository<TEntity, TIdentifierType>)Factory(
            typeof(EFReadRepository<TEntity, TIdentifierType>), DbContextArg)(_serviceProvider, [dbContext]);

        if (_applicationSettings.UseMiniProfiler)
        {
            repositoryInstance = (IReadRepository<TEntity, TIdentifierType>)Factory(
                typeof(EFReadRepositoryDecorator<TEntity, TIdentifierType>),
                [typeof(IReadRepository<TEntity, TIdentifierType>)])(_serviceProvider, [repositoryInstance]);
        }

        return repositoryInstance;
    }

    private static readonly Type[] DbContextArg = [typeof(DbContext)];

    private static readonly ConcurrentDictionary<Type, ObjectFactory> FactoryCache = new();

    /// <summary>
    /// Returns the cached compiled constructor for a closed repository type.
    /// <para>
    /// <see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/> matches the
    /// constructor by reflection on every call and caches nothing, so a request touching four
    /// aggregates paid four reflective activations. Each closed type here always takes the same
    /// argument shape, so the compiled <see cref="ObjectFactory"/> can be keyed by type alone.
    /// </para>
    /// </summary>
    private static ObjectFactory Factory(Type implementationType, Type[] argumentTypes) =>
        FactoryCache.GetOrAdd(
            implementationType,
            static (type, args) => ActivatorUtilities.CreateFactory(type, args),
            argumentTypes);
}
