using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Webhooks;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// Emits one startup Warning when <c>Webhooks:AllowInsecureTargetsInDevelopment</c> is set in a host
/// that is not Development (or has no registered environment, which reads as production): the flag is
/// ignored there, and an operator who set it expecting local targets to work should learn that once,
/// at startup, rather than from refused subscriptions.
/// </summary>
/// <param name="settings">The webhook options.</param>
/// <param name="services">Supplies the optional host environment.</param>
/// <param name="logger">Logger for the startup notice.</param>
internal sealed partial class InsecureWebhookTargetsNoticeService(
    IOptions<WebhookSettings> settings,
    IServiceProvider services,
    ILogger<InsecureWebhookTargetsNoticeService> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var environment = services.GetService<IHostEnvironment>();
        if (settings.Value.AllowInsecureTargetsInDevelopment && !WebhookInsecureTargets.AreAllowed(settings.Value, environment))
        {
            LogFlagIgnored(logger, environment?.EnvironmentName ?? "(none registered)");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Webhooks:AllowInsecureTargetsInDevelopment is set but this host's environment is {EnvironmentName}, not Development, so it is ignored: webhook targets must be https and resolve to public addresses only.")]
    private static partial void LogFlagIgnored(ILogger logger, string environmentName);
}
