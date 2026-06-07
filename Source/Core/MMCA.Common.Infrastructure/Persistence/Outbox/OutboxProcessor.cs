using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Background service that polls the outbox tables for unprocessed domain events and
/// dispatches them via <see cref="IDomainEventDispatcher"/>. Acts as a safety net:
/// events are normally dispatched in-process immediately after persistence, but if
/// that dispatch fails (e.g. process crash), this processor retries them.
/// <para>
/// Every relational physical data source in use by this host has its own
/// <c>OutboxMessages</c> table; each polling cycle drains them all. A host therefore only
/// processes the outboxes of its own databases — services with separate databases never race
/// for each other's messages.
/// </para>
/// </summary>
/// <param name="scopeFactory">Factory for creating DI scopes per processing cycle.</param>
/// <param name="logger">Logger for processing diagnostics.</param>
/// <param name="outboxOptions">Configurable outbox processing settings.</param>
/// <param name="outboxSignal">Signal to wait on between polling cycles for immediate wakeup.</param>
/// <param name="entityDataSourceRegistry">Registry enumerating the physical data sources in use.</param>
/// <param name="dataSourceResolver">Resolver for the configured outbox publish target.</param>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOutboxSignal outboxSignal,
    IEntityDataSourceRegistry entityDataSourceRegistry,
    IDataSourceResolver dataSourceResolver) : BackgroundService
{
    private readonly OutboxSettings _settings = outboxOptions.Value;

    private static readonly ActivitySource OutboxActivitySource = new("MMCA.Common.Outbox");
    private static readonly Meter OutboxMeter = new("MMCA.Common.Outbox");
    private static readonly Counter<long> DeadLetterCounter = OutboxMeter.CreateCounter<long>(
        "outbox.dead_letter.count",
        "messages",
        "Number of outbox messages dead-lettered due to unresolvable event types");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay so the application finishes initializing before we start polling.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        if (GetOutboxSources().Count == 0)
        {
            LogOutboxDisabled(logger);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessagesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown — exit gracefully.
                break;
            }
            catch (Exception ex)
            {
                LogProcessingError(logger, ex);
            }

            // Wait for a signal (new outbox entries written) or fall back to polling interval.
            await outboxSignal.WaitAsync(TimeSpan.FromSeconds(_settings.PollingIntervalSeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The relational physical sources whose outbox tables this host owns: every source backing a
    /// registered entity plus the configured publish target (Cosmos has no outbox table).
    /// Recomputed per cycle — cheap, and tolerant of module assemblies loading after startup.
    /// </summary>
    private List<DataSourceKey> GetOutboxSources()
    {
        IEnumerable<DataSourceKey> sources = entityDataSourceRegistry.GetPhysicalSourcesInUse()
            .Where(k => k.Engine != DataSource.CosmosDB);

        if (_settings.DataSource != DataSource.CosmosDB)
        {
            sources = sources.Append(dataSourceResolver.ResolveLogical(_settings.DataSource, _settings.DatabaseName));
        }

        return [.. sources.Distinct()];
    }

    private async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        foreach (var source in GetOutboxSources())
        {
            try
            {
                await ProcessSourceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreachable database must not starve the other sources' outboxes.
                LogSourceProcessingError(logger, source.ToString(), ex);
            }
        }
    }

    private async Task ProcessSourceAsync(DataSourceKey source, CancellationToken cancellationToken)
    {
        var sourceName = source.ToString();
        using var scope = scopeFactory.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<DbContexts.Factory.IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(source);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();
        var messageBus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var cutoff = DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(_settings.ProcessingDelaySeconds));

        var messages = await context.Set<OutboxMessage>()
            .Where(m => m.ProcessedOn == null && m.OccurredOn < cutoff && m.RetryCount < _settings.MaxRetries)
            .OrderBy(m => m.OccurredOn)
            .Take(_settings.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            return;
        }

        LogProcessingBatch(logger, messages.Count, sourceName);

        foreach (var message in messages)
        {
            using var activity = StartOutboxActivity(message, source);
            try
            {
                var domainEvent = message.DeserializeEvent();
                if (domainEvent is null)
                {
                    message.LastError = $"Cannot resolve type: {message.EventType}";
                    message.ProcessedOn = DateTime.UtcNow;
                    DeadLetterCounter.Add(1, new KeyValuePair<string, object?>("event_type", message.EventType));
                    LogDeadLetter(logger, message.Id, message.EventType);
                    continue;
                }

                // Integration events route through IMessageBus so the registered transport
                // (in-process for the monolith, MassTransit broker for extracted services)
                // determines delivery. Pure domain events keep the legacy in-process dispatch.
                if (domainEvent is IIntegrationEvent integrationEvent)
                {
                    await messageBus.PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await dispatcher.DispatchAsync([domainEvent], cancellationToken).ConfigureAwait(false);
                }

                message.ProcessedOn = DateTime.UtcNow;
                LogMessageProcessed(logger, message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                LogMessageRetry(logger, message.Id, message.RetryCount, ex);
            }
        }

        // Use the base DbContext.SaveChangesAsync to bypass audit stamping and event dispatch.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a new <see cref="Activity"/> linked to the original request's trace context
    /// stored in the outbox message. Returns <see langword="null"/> when no trace context
    /// was captured (e.g., messages written before this feature was added).
    /// </summary>
    private static Activity? StartOutboxActivity(OutboxMessage message, DataSourceKey source)
    {
        if (string.IsNullOrEmpty(message.TraceId) || string.IsNullOrEmpty(message.SpanId))
        {
            return null;
        }

        var parentContext = new ActivityContext(
            ActivityTraceId.CreateFromString(message.TraceId),
            ActivitySpanId.CreateFromString(message.SpanId),
            ActivityTraceFlags.Recorded);

        var activity = OutboxActivitySource.StartActivity(
            "OutboxProcess",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("messaging.outbox.message_id", message.Id.ToString());
        activity?.SetTag("messaging.outbox.event_type", message.EventType);
        activity?.SetTag("messaging.outbox.data_source", source.ToString());

        return activity;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox processor disabled: no relational data sources in use (Cosmos DB does not support the outbox table)")]
    private static partial void LogOutboxDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processor encountered an error")]
    private static partial void LogProcessingError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processing failed for data source {DataSourceName}")]
    private static partial void LogSourceProcessingError(ILogger logger, string dataSourceName, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Count} pending outbox messages from {DataSourceName}")]
    private static partial void LogProcessingBatch(ILogger logger, int count, string dataSourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({EventType}) dispatched successfully")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered: type not resolvable — {EventType}")]
    private static partial void LogDeadLetter(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} failed (attempt {RetryCount})")]
    private static partial void LogMessageRetry(ILogger logger, Guid messageId, int retryCount, Exception exception);
}
