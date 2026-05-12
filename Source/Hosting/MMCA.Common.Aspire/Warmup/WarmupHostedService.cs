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
/// </summary>
internal sealed partial class WarmupHostedService(
    IEnumerable<IWarmupTask> tasks,
    WarmupReadinessGate gate,
    ILogger<WarmupHostedService> logger) : BackgroundService
{
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
            await task.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            LogTaskCompleted(logger, task.Name, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
}
