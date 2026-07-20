using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Background service that periodically purges spent <see cref="OutboxMessage"/> rows older than
/// <see cref="OutboxSettings.RetentionDays"/> from every relational data source in use: both
/// <b>processed</b> rows and <b>dead-lettered</b> rows (retries exhausted, never delivered).
/// <para>
/// The <see cref="OutboxProcessor"/> only ever sets <c>ProcessedOn</c>; a message that exhausts
/// <see cref="OutboxSettings.MaxRetries"/> keeps <c>ProcessedOn</c> null forever, so without this
/// sweep the outbox table — which stores serialized event payloads that may contain personal data —
/// grows without bound (ADR-003 / ADR-005), and dead rows also linger in the pending index where
/// every poll re-scans them. Set <see cref="OutboxSettings.RetentionDays"/> to <c>0</c> to disable
/// purging.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating a DI scope per sweep.</param>
/// <param name="logger">Logger for cleanup diagnostics.</param>
/// <param name="outboxOptions">Configurable outbox settings (retention + sweep interval).</param>
/// <param name="messageBusOptions">Message-bus settings; used to gate inbox purging on <c>EnableInbox</c>.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured outbox publish target.</param>
/// <param name="timeProvider">Clock abstraction for the sweep interval and the retention cutoff; defaults to
/// <see cref="TimeProvider.System"/> so tests can drive the hour-scale loop deterministically.</param>
public sealed partial class OutboxCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxCleanupService> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOptions<MessageBusSettings> messageBusOptions,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly OutboxSettings _settings = outboxOptions.Value;
    private readonly bool _inboxEnabled = messageBusOptions.Value.EnableInbox;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
                await _timeProvider.Delay(interval, stoppingToken).ConfigureAwait(false);
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
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime.Subtract(TimeSpan.FromDays(_settings.RetentionDays));

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

                // Dead-lettered rows (retries exhausted) keep ProcessedOn null forever, so the
                // OutboxProcessor's poll excludes them (RetryCount < MaxRetries) but the processed
                // sweep above never reaches them: they accumulate without bound AND stay in the
                // ProcessedOn-IS-NULL pending index, re-scanned by every poll cycle. Purge them on
                // their own retention window (Outbox:DeadLetterRetentionDays, falling back to
                // RetentionDays), keyed on OccurredOn since they have no ProcessedOn.
                // This permanently abandons an undelivered event after that window; the failure is
                // recorded on the row (LastError), counted on the dead-letter metric, and logged at
                // Error by the processor at the moment of exhaustion.
                var deadLetterRetentionDays = _settings.DeadLetterRetentionDays > 0
                    ? _settings.DeadLetterRetentionDays
                    : _settings.RetentionDays;
                var deadLetterCutoff = _timeProvider.GetUtcNow().UtcDateTime
                    .Subtract(TimeSpan.FromDays(deadLetterRetentionDays));

                var deadLettered = await context.Set<OutboxMessage>()
                    .Where(m => m.ProcessedOn == null
                        && m.RetryCount >= _settings.MaxRetries
                        && m.OccurredOn < deadLetterCutoff)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (deadLettered > 0)
                {
                    LogDeadLetterPurged(logger, deadLettered, sourceName);
                }

                if (_inboxEnabled)
                {
                    await PurgeInboxAsync(context, cutoff, sourceName, cancellationToken).ConfigureAwait(false);
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

    private async Task PurgeInboxAsync(
        DbContext context,
        DateTime cutoff,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var inboxDeleted = await context.Set<InboxMessage>()
            .Where(m => m.ProcessedOn < cutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (inboxDeleted > 0)
        {
            LogInboxPurged(logger, inboxDeleted, sourceName);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purged {Count} dead-lettered (retries exhausted) outbox messages older than retention from {DataSourceName}")]
    private static partial void LogDeadLetterPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} processed inbox messages older than retention from {DataSourceName}")]
    private static partial void LogInboxPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup encountered an error")]
    private static partial void LogCleanupError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup failed for data source {DataSourceName}")]
    private static partial void LogSourcePurgeError(ILogger logger, string dataSourceName, Exception exception);
}
