using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.Tenancy;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;

/// <summary>
/// Background service that periodically purges spent <see cref="InternalCommandMessage"/> rows from
/// every relational data source in use.
/// <para>
/// <b>Completed</b> rows are deleted past <see cref="InternalCommandsSettings.RetentionDays"/>:
/// without this sweep the queue table, which stores serialized command payloads that may contain
/// personal data (ADR-003 / ADR-005), grows without bound and the pending index every poll re-scans
/// grows with it. Set the window to <c>0</c> to disable purging entirely.
/// </para>
/// <para>
/// <b>Dead-lettered</b> rows are deleted once
/// <see cref="InternalCommandsSettings.DeadLetterRetentionDays"/> has elapsed, and every such
/// deletion is logged at Warning with its count, because it destroys the only record that the work
/// was ever asked for. Set that window wider than the completed one to keep abandoned commands
/// available for diagnosis and requeue.
/// </para>
/// <para>
/// A separate service from <c>OutboxCleanupService</c> rather than one more table folded into it: the
/// queue has its own retention windows and its own enable flag, and a host that turns the queue off
/// must not lose its outbox sweep with it.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating a DI scope per sweep.</param>
/// <param name="logger">Logger for cleanup diagnostics.</param>
/// <param name="options">Configurable queue settings (retention plus sweep interval).</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured scheduling target.</param>
/// <param name="timeProvider">Clock abstraction for the sweep interval and the retention cutoff;
/// injected so tests can drive the hour-scale loop.</param>
/// <param name="tenancyOptions">Bound tenancy settings, used to discover tenants that keep their own
/// copy of a source, whose queue table the shared sweep never reaches.</param>
public sealed partial class InternalCommandCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<InternalCommandCleanupService> logger,
    IOptions<InternalCommandsSettings> options,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver,
    TimeProvider timeProvider,
    IOptions<TenancySettings>? tenancyOptions = null)
    : PeriodicBackgroundService(timeProvider, logger)
{
    private readonly InternalCommandsSettings _settings = options.Value;

    // Not the timeProvider parameter itself: the base constructor already receives it, and capturing
    // the same parameter into this type's state would be CS9107.
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <inheritdoc />
    protected override TimeSpan Interval => TimeSpan.FromHours(_settings.CleanupIntervalHours);

    /// <summary>
    /// Gets one full interval: the first sweep waits a whole interval so cleanup never competes with
    /// startup or migration work.
    /// </summary>
    protected override TimeSpan StartupDelay => Interval;

    /// <inheritdoc />
    protected override bool IsEnabled
    {
        get
        {
            if (_settings.RetentionDays <= 0)
            {
                LogCleanupDisabled(logger);
                return false;
            }

            return true;
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteCycleAsync(CancellationToken stoppingToken) => PurgeAsync(stoppingToken);

    /// <inheritdoc />
    protected override void LogCycleFailure(Exception exception) => LogCleanupError(logger, exception);

    /// <summary>
    /// Sweeps every target once. Internal so tests can drive one sweep without advancing the
    /// hour-scale loop.
    /// </summary>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    internal async Task PurgeAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.Subtract(TimeSpan.FromDays(_settings.RetentionDays));

        var deadLetterRetentionDays = _settings.DeadLetterRetentionDays > 0
            ? _settings.DeadLetterRetentionDays
            : _settings.RetentionDays;
        var deadLetterCutoff = now.Subtract(TimeSpan.FromDays(deadLetterRetentionDays));

        foreach (var target in GetTargets())
        {
            var sourceName = target.ToString();
            try
            {
                // The tenant is set before the context is asked for (see CreateTenantScope).
                using var scope = scopeFactory.CreateTenantScope(target);

                var context = scope.ServiceProvider.GetRequiredService<IDbContextFactory>()
                    .GetDbContext(target.Source);

                var deleted = await context.Set<InternalCommandMessage>()
                    .Where(c => c.ProcessedOn != null && c.ProcessedOn < cutoff)
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    LogPurged(logger, deleted, sourceName);
                }

                await SweepDeadLettersAsync(context, sourceName, deadLetterCutoff, cancellationToken)
                    .ConfigureAwait(false);
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
    /// Deletes the abandoned rows of one source past their own window, keyed on
    /// <c>DeadLetteredOn</c> rather than the completion stamp they never got.
    /// </summary>
    private async Task SweepDeadLettersAsync(
        ApplicationDbContext context,
        string sourceName,
        DateTime deadLetterCutoff,
        CancellationToken cancellationToken)
    {
        var deleted = await context.Set<InternalCommandMessage>()
            .Where(c => c.DeadLetteredOn != null && c.DeadLetteredOn < deadLetterCutoff)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (deleted > 0)
        {
            LogDeadLetterPurged(logger, deleted, sourceName);
        }
    }

    /// <summary>
    /// The relational physical sources whose queue tables this host owns: the same set the
    /// <c>InternalCommandProcessor</c> drains, expanded per tenant that keeps its own copy.
    /// </summary>
    internal List<TenantDataSourceTarget> GetTargets() =>
        TenantDataSourceTargets.ExpandRelational(
            entityDataSourceRegistry,
            dataSourceResolver,
            _settings.DataSource,
            _settings.DatabaseName,
            tenancyOptions?.Value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Internal command cleanup disabled: InternalCommands:RetentionDays is 0")]
    private static partial void LogCleanupDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} completed internal commands older than retention from {DataSourceName}")]
    private static partial void LogPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purged {Count} dead-lettered internal commands older than retention from {DataSourceName}")]
    private static partial void LogDeadLetterPurged(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command cleanup encountered an error")]
    private static partial void LogCleanupError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command cleanup failed for data source {DataSourceName}")]
    private static partial void LogSourcePurgeError(ILogger logger, string dataSourceName, Exception exception);
}
