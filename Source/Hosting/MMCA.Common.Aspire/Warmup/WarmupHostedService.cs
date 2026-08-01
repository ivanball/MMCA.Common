using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Aspire.Warmup;

/// <summary>
/// Background service that runs every registered <see cref="IWarmupTask"/> exactly once on
/// startup, in parallel, then opens the <see cref="WarmupReadinessGate"/>. The gate is opened
/// even if individual tasks fail — a stuck dependency must not keep the replica out of traffic
/// rotation forever; transient failures are absorbed by the existing Polly retry pipeline on
/// the first real request.
/// <para>
/// Each task is additionally bounded by <see cref="TaskTimeoutSeconds"/>. Failure was already
/// harmless, but a task that neither completes nor throws used to leave <c>Task.WhenAll</c>
/// pending forever and the gate closed with it, so the replica never entered rotation at all:
/// strictly worse than serving a cold one. The timeout turns that case into the existing
/// log-and-continue failure path, which is the whole point of the gate.
/// </para>
/// </summary>
/// <param name="tasks">The warm-up tasks to run once at startup.</param>
/// <param name="gate">The readiness gate opened once every task has finished, failed, or timed out.</param>
/// <param name="logger">Logger for warm-up diagnostics.</param>
/// <param name="timeProvider">Clock abstraction backing the per-task timeout; defaults to
/// <see cref="TimeProvider.System"/>.</param>
/// <param name="taskTimeout">Overrides <see cref="TaskTimeoutSeconds"/> so tests can exercise the
/// timeout path without waiting two minutes; production always takes the default.</param>
internal sealed partial class WarmupHostedService(
    IEnumerable<IWarmupTask> tasks,
    WarmupReadinessGate gate,
    ILogger<WarmupHostedService> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? taskTimeout = null) : BackgroundService
{
    /// <summary>
    /// Ceiling on a single warm-up task, in seconds. This is a backstop, not a latency budget:
    /// the built-in OpenID Connect metadata task is already capped at 90s by the standard
    /// resilience pipeline, so a task reaching this limit is one that bypassed those defaults or
    /// is waiting on something that will never arrive. 120s leaves headroom above the resilience
    /// total while still opening the gate inside a normal rollout window.
    /// </summary>
    private const int TaskTimeoutSeconds = 120;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _taskTimeout = taskTimeout ?? TimeSpan.FromSeconds(TaskTimeoutSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var overall = Stopwatch.StartNew();

        try
        {
            await Task.WhenAll(tasks.Select(task => RunOneAsync(task, stoppingToken)))
                .ConfigureAwait(false);
        }
        finally
        {
            gate.MarkReady();
            LogWarmupComplete(logger, overall.ElapsedMilliseconds);
        }
    }

    private async Task RunOneAsync(IWarmupTask task, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await task.ExecuteAsync(cancellationToken)
                .WaitAsync(_taskTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            LogTaskCompleted(logger, task.Name, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            // Same outcome as a failure: the abandoned task keeps running detached, the gate
            // opens, and the dependency is retried lazily on first use.
            LogTaskTimedOut(logger, ex, task.Name, sw.ElapsedMilliseconds, _taskTimeout.TotalSeconds);
        }
#pragma warning disable CA1031 // warm-up failures must never crash the host
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTaskFailed(logger, ex, task.Name, sw.ElapsedMilliseconds);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Warm-up task {TaskName} completed in {ElapsedMs}ms.")]
    private static partial void LogTaskCompleted(ILogger logger, string taskName, long elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Warm-up task {TaskName} failed after {ElapsedMs}ms — will retry lazily on first use.")]
    private static partial void LogTaskFailed(ILogger logger, Exception exception, string taskName, long elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Warm-up complete in {ElapsedMs}ms — readiness gate open.")]
    private static partial void LogWarmupComplete(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Warm-up task {TaskName} timed out after {ElapsedMs}ms (limit {TimeoutSeconds}s): abandoned so the readiness gate can open; will retry lazily on first use.")]
    private static partial void LogTaskTimedOut(ILogger logger, Exception exception, string taskName, long elapsedMs, double timeoutSeconds);
}
