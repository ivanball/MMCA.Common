using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// Emits a single startup Warning when a host runs broker messaging with consumer-side inbox
/// deduplication explicitly turned OFF (the <see cref="NoOpInboxStore"/> registration). A silently
/// disabled safety feature is indistinguishable from an enabled one until the first duplicate side
/// effect reaches a customer, so the off state is made loud exactly once, at startup, where it
/// costs one log line and nothing per message.
/// <para>
/// Since the inbox defaults to ON for every broker transport
/// (<c>MessageBusSettings.IsInboxEnabled</c>), reaching this service means the host set
/// <c>MessageBus:EnableInbox=false</c> deliberately: the warning names it as an opt-out and states
/// the consequence, rather than reading as a nudge about a default.
/// </para>
/// </summary>
/// <param name="logger">Logger for the startup warning.</param>
internal sealed partial class InboxDisabledWarningService(ILogger<InboxDisabledWarningService> logger)
    : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogInboxDisabled(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Broker messaging is running with consumer-side inbox deduplication explicitly OFF (MessageBus:EnableInbox=false, NoOpInboxStore). The broker default is ON. Broker delivery is at-least-once, so every redelivered message will run its handlers again and duplicate their side effects until each handler is idempotent on its own. Remove the setting (or set it to true) to restore deduplication; the InboxMessages table is already part of the model.")]
    private static partial void LogInboxDisabled(ILogger logger);
}
