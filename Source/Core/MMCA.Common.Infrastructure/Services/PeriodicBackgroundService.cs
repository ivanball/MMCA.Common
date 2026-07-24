using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Base class for fixed-interval background sweeps: an optional enablement gate, a short
/// startup delay so the host finishes initializing, then a loop of
/// <see cref="ExecuteCycleAsync"/> + interval wait until shutdown. A failing cycle is logged
/// and NEVER kills the loop; the next interval retries. All waits go through the injected
/// <see cref="TimeProvider"/> so tests can drive the loop deterministically with a fake clock.
/// <para>
/// Use this for periodic reconciliation/cleanup work (stuck-state sweeps, cache refreshes,
/// external-system polling). It is deliberately NOT used by the outbox processor, whose
/// signal-driven smart wait does not fit a fixed interval.
/// </para>
/// </summary>
/// <param name="timeProvider">Clock used for the startup delay and interval waits.</param>
/// <param name="logger">Logger receiving cycle failures and lifecycle messages.</param>
public abstract partial class PeriodicBackgroundService(
    TimeProvider timeProvider,
    ILogger logger) : BackgroundService
{
    /// <summary>Gets the wait between cycles.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>
    /// Gets the delay before the first cycle, so the application finishes initializing
    /// before background work starts. Override to shorten in tests.
    /// </summary>
    protected virtual TimeSpan StartupDelay => TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets a value indicating whether the service should run at all. Evaluated once at
    /// startup; when <see langword="false"/> the service logs and exits (e.g. a feature
    /// toggle is off or a required credential is not configured).
    /// </summary>
    protected virtual bool IsEnabled => true;

    /// <summary>Runs one sweep cycle. Exceptions are logged and do not stop the loop.</summary>
    /// <param name="stoppingToken">Canceled when the host shuts down.</param>
    protected abstract Task ExecuteCycleAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            LogDisabled(logger, GetType().Name);
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown mid-cycle.
                break;
            }
            catch (Exception ex)
            {
                LogCycleError(logger, GetType().Name, ex);
            }

            try
            {
                await Task.Delay(Interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{ServiceName} is disabled; the periodic sweep will not run")]
    private static partial void LogDisabled(ILogger logger, string serviceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "{ServiceName} cycle failed; it will retry on the next interval")]
    private static partial void LogCycleError(ILogger logger, string serviceName, Exception ex);
}
