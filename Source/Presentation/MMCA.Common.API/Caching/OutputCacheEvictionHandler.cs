using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.IntegrationEvents;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Consumes <see cref="OutputCacheEvictionRequested"/> and drops the named tags from THIS host's
/// output cache. Registered with <c>AddOutputCacheEvictionHandler()</c> and reached through the
/// standard broker path: the host calls
/// <c>RegisterOutputCacheEvictionConsumer()</c> inside its <c>AddBrokerMessaging</c> configuration,
/// which wires the generic <c>IntegrationEventConsumer&lt;OutputCacheEvictionRequested&gt;</c> onto
/// this handler. No MassTransit type appears here, so the handler is equally reachable from the
/// in-process dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-tag best effort.</b> Each tag is evicted independently and a failure is logged and counted
/// rather than rethrown. Rethrowing would hand the message back to the retry policy and redeliver
/// it, re-evicting every tag that already succeeded, and would eventually dead-letter a message
/// whose only consequence is a cache entry that expires on its own TTL anyway. A failed eviction is
/// a staleness window, not a lost fact.
/// </para>
/// <para>
/// <b>Cancellation is not swallowed.</b> An <see cref="OperationCanceledException"/> from host
/// shutdown propagates, so MassTransit sees the shutdown rather than an acked message.
/// </para>
/// </remarks>
/// <param name="outputCacheStore">This host's output-cache store.</param>
/// <param name="logger">Logger for eviction diagnostics.</param>
public sealed partial class OutputCacheEvictionHandler(
    IOutputCacheStore outputCacheStore,
    ILogger<OutputCacheEvictionHandler> logger)
    : IIntegrationEventHandler<OutputCacheEvictionRequested>
{
    /// <inheritdoc />
    public async Task HandleAsync(
        OutputCacheEvictionRequested integrationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        foreach (var tag in integrationEvent.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            try
            {
                await outputCacheStore.EvictByTagAsync(tag, cancellationToken).ConfigureAwait(false);
                LogEvicted(logger, tag);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately broad (CA1031/S2221 are suggestions here): an eviction store that
                // throws must not turn a coherence hint into a dead-lettered message.
                OutputCacheMetrics.RecordEvictionFailure(tag);
                LogEvictionFailed(logger, tag, ex);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Evicted output-cache tag '{CacheTag}' in response to a cross-service eviction request")]
    private static partial void LogEvicted(ILogger logger, string cacheTag);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to evict output-cache tag '{CacheTag}'; responses carrying it stay cached until their own TTL expires")]
    private static partial void LogEvictionFailed(ILogger logger, string cacheTag, Exception exception);
}
