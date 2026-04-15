using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// Background service that polls the outbox table for unprocessed domain events and
/// dispatches them via <see cref="IDomainEventDispatcher"/>. Acts as a safety net:
/// events are normally dispatched in-process immediately after persistence, but if
/// that dispatch fails (e.g. process crash), this processor retries them.
/// </summary>
/// <param name="scopeFactory">Factory for creating DI scopes per processing cycle.</param>
/// <param name="logger">Logger for processing diagnostics.</param>
/// <param name="outboxOptions">Configurable outbox processing settings.</param>
/// <param name="outboxSignal">Signal to wait on between polling cycles for immediate wakeup.</param>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor> logger,
    IOptions<OutboxSettings> outboxOptions,
    IOutboxSignal outboxSignal) : BackgroundService
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
        if (_settings.DataSource == Application.Interfaces.Infrastructure.DataSource.CosmosDB)
        {
            LogOutboxDisabled(logger, _settings.DataSource);
            return;
        }

        // Brief startup delay so the application finishes initializing before we start polling.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

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

    private async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<DbContexts.Factory.IDbContextFactory>();
        var context = dbContextFactory.GetDbContext(_settings.DataSource);
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

        LogProcessingBatch(logger, messages.Count);

        foreach (var message in messages)
        {
            using var activity = StartOutboxActivity(message);
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
    private static Activity? StartOutboxActivity(OutboxMessage message)
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

        return activity;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox processor disabled: {DataSource} does not support the outbox table")]
    private static partial void LogOutboxDisabled(ILogger logger, Application.Interfaces.Infrastructure.DataSource dataSource);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox processor encountered an error")]
    private static partial void LogProcessingError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Processing {Count} pending outbox messages")]
    private static partial void LogProcessingBatch(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox message {MessageId} ({EventType}) dispatched successfully")]
    private static partial void LogMessageProcessed(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered: type not resolvable — {EventType}")]
    private static partial void LogDeadLetter(ILogger logger, Guid messageId, string eventType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} failed (attempt {RetryCount})")]
    private static partial void LogMessageRetry(ILogger logger, Guid messageId, int retryCount, Exception exception);
}
