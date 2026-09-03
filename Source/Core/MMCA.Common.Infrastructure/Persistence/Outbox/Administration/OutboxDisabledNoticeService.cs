using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;

namespace MMCA.Common.Infrastructure.Persistence.Outbox.Administration;

/// <summary>
/// Emits a single startup Information line when a host runs without the transactional outbox (the
/// in-process default, or an explicit <c>MessageBus:EnableOutbox=false</c>). Neither
/// <see cref="OutboxProcessor"/> nor <see cref="OutboxCleanupService"/> is registered in that mode,
/// so the delivery guarantee changes: events reach their handlers synchronously inside the raising
/// process and a crash between the commit and the dispatch loses them. That is the right trade for a
/// single-process application, and the wrong one to discover from an absent background service, so
/// the posture is stated once at startup for the cost of one log line.
/// <para>
/// Information rather than Warning (the inbox's <c>InboxDisabledWarningService</c> level): this is
/// the DEFAULT posture of an in-process host, not an opt-out of a safety feature, and a warning on
/// every small application's startup would train operators to ignore the category.
/// </para>
/// </summary>
/// <param name="logger">Logger for the startup notice.</param>
internal sealed partial class OutboxDisabledNoticeService(ILogger<OutboxDisabledNoticeService> logger)
    : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogOutboxDisabled(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox disabled: in-process messaging dispatches events synchronously. No OutboxMessages rows are written and neither OutboxProcessor nor OutboxCleanupService is running, so an event whose handler fails is not retried and a crash between the commit and the dispatch loses it. Set MessageBus:EnableOutbox=true to restore store-and-forward delivery; the OutboxMessages table is already part of the model, so no migration is needed.")]
    private static partial void LogOutboxDisabled(ILogger logger);
}
