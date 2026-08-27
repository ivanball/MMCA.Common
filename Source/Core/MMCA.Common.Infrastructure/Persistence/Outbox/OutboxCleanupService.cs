using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Inbox;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Background service that periodically purges spent <see cref="OutboxMessage"/> rows older than
/// <see cref="OutboxSettings.RetentionDays"/> from every relational data source in use.
/// <para>
/// <b>Processed</b> rows are always deleted past the window: without this sweep the outbox table —
/// which stores serialized event payloads that may contain personal data — grows without bound
/// (ADR-003 / ADR-005). Set <see cref="OutboxSettings.RetentionDays"/> to <c>0</c> to disable
/// purging entirely.
/// </para>
/// <para>
/// <b>Dead-lettered</b> rows (retries exhausted, never delivered) are KEPT by default and only
/// counted, loudly, once per source per sweep. Deleting an undelivered event is unrecoverable, so
/// it takes an explicit <see cref="OutboxSettings.PurgeDeadLetters"/>; the alternative to deletion
/// is replaying them through <c>IOutboxAdministration</c>.
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
/// <param name="tenancyOptions">
/// Bound tenancy settings, used only to discover tenants that keep their own copy of a source: each
/// such database has its own outbox and inbox tables, which the shared sweep never reaches.
/// </param>
public sealed partial class OutboxCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxCleanupService> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOptions<MessageBusSettings> messageBusOptions,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    TimeProvider? timeProvider = null,
    IOptions<TenancySettings>? tenancyOptions = null) : BackgroundService
{
    private readonly OutboxSettings _settings = outboxOptions.Value;
    private readonly bool _inboxEnabled = messageBusOptions.Value.IsInboxEnabled;
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

        foreach (var target in GetRelationalTargets())
        {
            var sourceName = target.ToString();
            try
            {
                using var scope = scopeFactory.CreateScope();

                // Set before the context is asked for: the tenant is what routes the scoped factory
                // to this tenant's own database.
                if (target.TenantId is { } tenantId)
                {
                    scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                }

                var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory>();
                var context = dbContextFactory.GetDbContext(target.Source);

                var deleted = await context.Set<OutboxMessage>()
                    .Where(m => m.ProcessedOn != null && m.ProcessedOn < cutoff)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    LogPurged(logger, deleted, sourceName);
                }

                await SweepDeadLettersAsync(context, sourceName, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Handles the dead-lettered rows (retries exhausted) of one source. They keep <c>ProcessedOn</c>
    /// null forever, so the <see cref="OutboxProcessor"/>'s poll excludes them
    /// (<c>RetryCount &lt; MaxRetries</c>) but the processed sweep never reaches them either: they
    /// accumulate AND stay in the <c>ProcessedOn IS NULL</c> pending index that every poll re-scans.
    /// <para>
    /// What happens to them past their window (<c>Outbox:DeadLetterRetentionDays</c>, falling back
    /// to <c>RetentionDays</c>, keyed on <c>OccurredOn</c> since they have no <c>ProcessedOn</c>) is
    /// a decision, not a default: deleting an undelivered event destroys the only record that it was
    /// ever raised, and it is the one cleanup action nothing can undo. So the purge is OPT-IN
    /// (<c>Outbox:PurgeDeadLetters</c>). Left off, the sweep counts them instead and logs one
    /// Warning per source per cycle, which keeps the pile visible until an operator either replays
    /// it (<c>IOutboxAdministration.ReplayDeadLettersAsync</c>) or turns the purge on.
    /// </para>
    /// </summary>
    private async Task SweepDeadLettersAsync(
        ApplicationDbContext context,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var deadLetterRetentionDays = _settings.DeadLetterRetentionDays > 0
            ? _settings.DeadLetterRetentionDays
            : _settings.RetentionDays;
        var deadLetterCutoff = _timeProvider.GetUtcNow().UtcDateTime
            .Subtract(TimeSpan.FromDays(deadLetterRetentionDays));

        var expired = context.Set<OutboxMessage>()
            .Where(m => m.ProcessedOn == null
                && m.RetryCount >= _settings.MaxRetries
                && m.OccurredOn < deadLetterCutoff);

        if (!_settings.PurgeDeadLetters)
        {
            var retained = await expired.LongCountAsync(cancellationToken).ConfigureAwait(false);
            if (retained > 0)
            {
                LogDeadLettersRetained(logger, retained, sourceName);
            }

            return;
        }

        var deadLettered = await expired.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (deadLettered > 0)
        {
            LogDeadLetterPurged(logger, deadLettered, sourceName);
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

    /// <summary>
    /// The units this sweep visits: every owned source against the shared database, plus one extra
    /// unit per tenant that keeps its own copy of a source (whose outbox and inbox tables live in a
    /// database nothing else opens).
    /// </summary>
    internal List<TenantDataSourceTarget> GetRelationalTargets() =>
        TenantDataSourceTargets.Expand(GetRelationalSources(), tenancyOptions?.Value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox cleanup disabled: Outbox:RetentionDays is 0")]
    private static partial void LogCleanupDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} processed outbox messages older than retention from {DataSourceName}")]
    private static partial void LogPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purged {Count} dead-lettered (retries exhausted) outbox messages older than retention from {DataSourceName}")]
    private static partial void LogDeadLetterPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} dead-lettered outbox messages in {DataSourceName} are past their retention window and are being KEPT (Outbox:PurgeDeadLetters is false). Replay them with IOutboxAdministration.ReplayDeadLettersAsync once the cause is fixed, or set Outbox:PurgeDeadLetters=true to delete them")]
    private static partial void LogDeadLettersRetained(ILogger logger, long count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} processed inbox messages older than retention from {DataSourceName}")]
    private static partial void LogInboxPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup encountered an error")]
    private static partial void LogCleanupError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox cleanup failed for data source {DataSourceName}")]
    private static partial void LogSourcePurgeError(ILogger logger, string dataSourceName, Exception exception);
}
