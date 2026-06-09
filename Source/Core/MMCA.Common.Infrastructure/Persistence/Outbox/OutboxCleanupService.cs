using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Background service that periodically purges <b>processed</b> <see cref="OutboxMessage"/> rows
/// older than <see cref="OutboxSettings.RetentionDays"/> from every relational data source in use.
/// <para>
/// The <see cref="OutboxProcessor"/> only ever sets <c>ProcessedOn</c>; without this sweep the
/// outbox table — which stores serialized event payloads that may contain personal data — grows
/// without bound (ADR-003 / ADR-005). Set <see cref="OutboxSettings.RetentionDays"/> to <c>0</c>
/// to disable purging.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating a DI scope per sweep.</param>
/// <param name="logger">Logger for cleanup diagnostics.</param>
/// <param name="outboxOptions">Configurable outbox settings (retention + sweep interval).</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured outbox publish target.</param>
public sealed partial class OutboxCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxCleanupService> logger,
    IOptions<OutboxSettings> outboxOptions,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver) : BackgroundService
{
    private readonly OutboxSettings _settings = outboxOptions.Value;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_settings.RetentionDays <= 0)
        {
            LogCleanupDisabled(logger);
            return;
        }

        var interval = TimeSpan.FromHours(_settings.CleanupIntervalHours);

        // Wait one interval before the first sweep so cleanup never competes with startup or
        // migration work, then sweep on each interval until shutdown.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await PurgeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogCleanupError(logger, ex);
            }
        }
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromDays(_settings.RetentionDays));

        foreach (var source in GetRelationalSources())
        {
            var sourceName = source.ToString();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
                var context = dbContextFactory.GetDbContext(source);

                var deleted = await context.Set<OutboxMessage>()
                    .Where(m => m.ProcessedOn != null && m.ProcessedOn < cutoff)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    LogPurged(logger, deleted, sourceName);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable database must not stop the others from being purged.
                LogSourcePurgeError(logger, sourceName, ex);
            }
        }
    }

    /// <summary>
    /// The relational physical sources whose outbox tables this host owns — the same set the
    /// <see cref="OutboxProcessor"/> drains (every source backing a registered entity plus the
    /// configured publish target; Cosmos has no outbox table).
    /// </summary>
    private List<DataSourceKey> GetRelationalSources()
    {
        IEnumerable<DataSourceKey> sources = entityDataSourceRegistry.GetPhysicalSourcesInUse()
            .Where(k => k.Engine != DataSource.CosmosDB);

        if (_settings.DataSource != DataSource.CosmosDB)
        {
            sources = sources.Append(dataSourceResolver.ResolveLogical(_settings.DataSource, _settings.DatabaseName));
        }

        return [.. sources.Distinct()];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox cleanup disabled: Outbox:RetentionDays is 0")]
    private static partial void LogCleanupDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} processed outbox messages older than retention from {DataSourceName}")]
    private static partial void LogPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup encountered an error")]
    private static partial void LogCleanupError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup failed for data source {DataSourceName}")]
    private static partial void LogSourcePurgeError(ILogger logger, string dataSourceName, Exception exception);
}
