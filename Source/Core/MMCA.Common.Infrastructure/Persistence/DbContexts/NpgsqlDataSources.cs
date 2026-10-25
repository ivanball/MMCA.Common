using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts;

/// <summary>
/// The shared <see cref="NpgsqlDataSource"/> cache behind <see cref="INpgsqlDataSourceConfigurator"/>.
/// The cache is the container itself: <c>AddNpgsqlDataSourceConfigurator</c> registers a keyed
/// singleton under <see cref="KeyedService.AnyKey"/> whose key is the connection string, so the
/// container builds one instance per distinct connection string, hands every caller the same one and
/// disposes it with itself.
/// <para>
/// Keying by the connection string is what lets MMCA.Common.Aspire's PostgreSQL readiness check reach
/// the same instance without referencing this package: both sides name only an Npgsql type and a
/// string. Nothing is registered until a configurator is added, so a host without one resolves
/// <see langword="null"/> and keeps the connection-string path.
/// </para>
/// </summary>
internal static class NpgsqlDataSources
{
    /// <summary>
    /// The shared data source for <paramref name="connectionString"/>, or <see langword="null"/> when no
    /// configurator is registered (or the provider cannot resolve keyed services, like the design-time
    /// provider behind <c>dotnet ef</c>).
    /// </summary>
    /// <param name="serviceProvider">The provider the context was created from.</param>
    /// <param name="connectionString">The physical source's connection string.</param>
    /// <returns>The shared data source, or <see langword="null"/>.</returns>
    internal static NpgsqlDataSource? Resolve(IServiceProvider serviceProvider, string connectionString) =>
        serviceProvider is IKeyedServiceProvider keyed && !string.IsNullOrWhiteSpace(connectionString)
            ? keyed.GetKeyedService(typeof(NpgsqlDataSource), connectionString) as NpgsqlDataSource
            : null;

    /// <summary>
    /// The keyed-singleton factory: builds the data source for one connection string and runs every
    /// registered configurator on its builder. A key that is not a connection string resolves nothing,
    /// so an unrelated keyed lookup of <see cref="NpgsqlDataSource"/> is not answered by this one.
    /// </summary>
    /// <param name="serviceProvider">The root provider.</param>
    /// <param name="key">The requested key: the connection string.</param>
    /// <returns>The configured data source, or <see langword="null"/> for a non-string key.</returns>
    internal static NpgsqlDataSource? Create(IServiceProvider serviceProvider, object? key)
    {
        if (key is not string connectionString || string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        foreach (var configurator in serviceProvider.GetServices<INpgsqlDataSourceConfigurator>())
        {
            configurator.Configure(builder);
        }

        return builder.Build();
    }
}
