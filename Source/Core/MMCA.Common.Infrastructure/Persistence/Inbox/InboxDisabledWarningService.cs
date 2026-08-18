using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// Emits a single startup Warning when a host enables broker messaging but leaves consumer-side
/// inbox deduplication off (the <see cref="NoOpInboxStore"/> registration). A silently disabled
/// safety feature is indistinguishable from an enabled one until the first duplicate side effect
/// reaches a customer, so the off state is made loud exactly once, at startup, where it costs one
/// log line and nothing per message.
/// <para>
/// Registered by <c>AddBrokerMessaging</c> only on the disabled branch, so a host that turns the
/// inbox on never sees this service at all.
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
        Message = "Broker messaging is enabled but consumer-side inbox deduplication is OFF (NoOpInboxStore). Broker delivery is at-least-once, so a redelivered message will run its handlers again and duplicate their side effects. Enable it with MessageBus:EnableInbox=true; the InboxMessages table is already part of the model.")]
    private static partial void LogInboxDisabled(ILogger logger);
}
