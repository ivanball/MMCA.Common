using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;

/// <summary>
/// Emits a single startup Information line when a host runs with <c>InternalCommands:Enabled=false</c>.
/// Neither the processor nor the cleanup sweep is registered in that mode, so anything scheduled
/// through <c>IInternalCommandScheduler</c> is written and then never executed by THIS host. That is
/// a legitimate posture (a web front end that queues work for a dedicated worker) and the wrong thing
/// to discover from an absent background service, so it is stated once at startup for the cost of one
/// log line.
/// <para>
/// Information rather than Warning, matching <c>OutboxDisabledNoticeService</c>: a deliberate
/// deployment split is not a safety opt-out, and a warning on every front end's startup would train
/// operators to ignore the category.
/// </para>
/// </summary>
/// <param name="logger">Logger for the startup notice.</param>
internal sealed partial class InternalCommandsDisabledNoticeService(
    ILogger<InternalCommandsDisabledNoticeService> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogQueueDisabled(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Internal commands disabled: this host writes InternalCommands rows but runs neither InternalCommandProcessor nor InternalCommandCleanupService, so scheduled work is executed only by a host that has InternalCommands:Enabled=true against the same databases. Set InternalCommands:Enabled=true to drain the queue here; the InternalCommands table is already part of the model, so no migration is needed.")]
    private static partial void LogQueueDisabled(ILogger logger);
}
