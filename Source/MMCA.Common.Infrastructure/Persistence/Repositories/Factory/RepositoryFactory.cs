using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Settings;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Infrastructure.Persistence.Repositories.Factory;

/// <summary>
/// Creates repository instances, conditionally wrapping them in MiniProfiler decorators
/// when profiling is enabled in <see cref="IApplicationSettings"/>.
/// </summary>
public sealed class RepositoryFactory(IServiceProvider serviceProvider, IApplicationSettings applicationSettings) : IRepositoryFactory
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IApplicationSettings _applicationSettings = applicationSettings;

    /// <inheritdoc />
    /// <remarks>
    /// When <see cref="IApplicationSettings.UseMiniProfiler"/> is <see langword="true"/>, the base
    /// <see cref="EFRepository{TEntity,TIdentifierType}"/> is wrapped in an
    /// <see cref="EFRepositoryDecorator{TEntity,TIdentifierType}"/> that records timing data.
    /// </remarks>
    public IRepository<TEntity, TIdentifierType> Create<TEntity, TIdentifierType>(
        DbContext dbContext)
        where TEntity : AuditableAggregateRootEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        IRepository<TEntity, TIdentifierType> repositoryInstance;
        repositoryInstance = ActivatorUtilities.CreateInstance<EFRepository<TEntity, TIdentifierType>>(
            _serviceProvider, dbContext);

        if (_applicationSettings.UseMiniProfiler)
        {
            repositoryInstance = ActivatorUtilities.CreateInstance<EFRepositoryDecorator<TEntity, TIdentifierType>>(
                _serviceProvider, repositoryInstance);
        }

        return repositoryInstance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// When <see cref="IApplicationSettings.UseMiniProfiler"/> is <see langword="true"/>, the base
    /// <see cref="EFReadRepository{TEntity,TIdentifierType}"/> is wrapped in an
    /// <see cref="EFReadRepositoryDecorator{TEntity,TIdentifierType}"/> that records timing data.
    /// </remarks>
    public IReadRepository<TEntity, TIdentifierType> CreateReadOnly<TEntity, TIdentifierType>(
        DbContext dbContext)
        where TEntity : AuditableBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        IReadRepository<TEntity, TIdentifierType> repositoryInstance;
        repositoryInstance = ActivatorUtilities.CreateInstance<EFReadRepository<TEntity, TIdentifierType>>(
            _serviceProvider, dbContext);

        if (_applicationSettings.UseMiniProfiler)
        {
            repositoryInstance = ActivatorUtilities.CreateInstance<EFReadRepositoryDecorator<TEntity, TIdentifierType>>(
                _serviceProvider, repositoryInstance);
        }

        return repositoryInstance;
    }
}
