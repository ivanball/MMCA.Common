using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

namespace MMCA.Common.Infrastructure.Notifications;

/// <summary>
/// Emits one startup Warning when push notifications are registered and no
/// <see cref="IChannelJoinAuthorizer"/> is, because <see cref="NotificationHub"/> then lets any
/// authenticated caller join any channel whose key has the configured shape. Join behavior is not
/// changed; the warning only makes the open posture visible once, at startup.
/// </summary>
/// <param name="serviceProviderIsService">Answers whether an authorizer is registered, without resolving one.</param>
/// <param name="logger">Logger for the startup warning.</param>
internal sealed partial class ChannelJoinAuthorizerMissingWarningService(
    IServiceProviderIsService serviceProviderIsService,
    ILogger<ChannelJoinAuthorizerMissingWarningService> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Checked at start, not at registration: a host may register its authorizer after
        // AddPushNotifications.
        if (!serviceProviderIsService.IsService(typeof(IChannelJoinAuthorizer)))
        {
            LogNoChannelJoinAuthorizer(logger, nameof(IChannelJoinAuthorizer));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Push notifications are registered without an {InterfaceName}: any authenticated caller can join any well-formed channel key on the notification hub and receive what is published to it. Register one if any channel is not meant for every signed-in user.")]
    private static partial void LogNoChannelJoinAuthorizer(ILogger logger, string interfaceName);
}
