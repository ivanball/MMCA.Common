using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Adapter implementing EF Core's <see cref="IDbContextFactory{TContext}"/> for
/// <see cref="ApplicationDbContext"/>. Resolves the default data source from configuration
/// (<c>DefaultDataSource</c> or <c>DataSource</c> keys, falling back to SQL Server)
/// and delegates to the appropriate provider-specific factory.
/// </summary>
public sealed class ApplicationDbContextEFFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly DataSource _defaultDataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplicationDbContextEFFactory"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider to resolve provider-specific context factories.</param>
    /// <param name="configuration">Application configuration. Reads <c>DefaultDataSource</c> then <c>DataSource</c> keys.</param>
    public ApplicationDbContextEFFactory(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        // Look up "DefaultDataSource" first, fall back to "DataSource", default to SQLServer.
        var defaultDataSourceStr = configuration?["DefaultDataSource"] ?? configuration?["DataSource"] ?? DataSource.SQLServer.ToString();
        _defaultDataSource = Enum.TryParse<DataSource>(defaultDataSourceStr, true, out var ds) ? ds : DataSource.SQLServer;
    }

    /// <inheritdoc />
    public ApplicationDbContext CreateDbContext() => _defaultDataSource switch
    {
        DataSource.CosmosDB => _serviceProvider.GetRequiredService<IDbContextFactory<CosmosDbContext>>().CreateDbContext(),
        DataSource.Sqlite => _serviceProvider.GetRequiredService<IDbContextFactory<SqliteDbContext>>().CreateDbContext(),
        DataSource.SQLServer => _serviceProvider.GetRequiredService<IDbContextFactory<SQLServerDbContext>>().CreateDbContext(),
        _ => throw new InvalidOperationException($"Unsupported default DataSource '{_defaultDataSource}'")
    };
}
