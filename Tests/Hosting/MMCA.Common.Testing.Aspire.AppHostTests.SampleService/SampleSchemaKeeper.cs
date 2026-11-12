using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

namespace MMCA.Common.Testing.Aspire.AppHostTests.SampleService;

/// <summary>
/// Keeps the outage scenario's schema in place: once a second it calls
/// <see cref="IDbContextFactory.EnsureCreatedAsync"/>, which is a no-op while the tables exist and
/// recreates them when they do not.
/// <para>
/// Test scaffolding, needed because Aspire recreates a container on start: a SQL Server container
/// with no data volume comes back from a stop and start with no tables. It also removes the start-up
/// race against a SQL Server that is not up yet, and makes the outage tests order-independent. A
/// failure while the database is unreachable is expected and swallowed; the next tick tries again.
/// </para>
/// </summary>
/// <param name="scopeFactory">Creates the scope each tick resolves the context factory from.</param>
/// <param name="logger">Logs a failed tick at Debug.</param>
internal sealed partial class SampleSchemaKeeper(IServiceScopeFactory scopeFactory, ILogger<SampleSchemaKeeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
                var resolver = scope.ServiceProvider.GetRequiredService<IDataSourceResolver>();

                // The sample maps no entity, so no source is "in use" until a context is materialized:
                // resolve the Default SQL Server context (the one holding the outbox) first, so
                // EnsureCreatedAsync has a source to create.
                _ = contexts.GetDbContext(resolver.ResolveLogical(DataSource.SQLServer, DataSourceKey.DefaultName));
                await contexts.EnsureCreatedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Schema keeper tick failed; the database is not reachable yet.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
