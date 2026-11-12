using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// The webhook delivery lane: delivery rows (<see cref="DeliverWebhookInternalCommand"/>) are drained by
/// their own sequential <see cref="InternalCommandLaneProcessor"/>, never by the shared internal-command
/// loop, so a slow or failing receiver cannot delay the other internal commands. Rows are keyed by
/// subscription, so a failing subscription is cooled down on its own.
/// </summary>
internal static class WebhookDeliveryLane
{
    /// <summary>The lane name, emitted as the <c>lane</c> metric tag and log value.</summary>
    internal const string LaneName = "webhooks";

    /// <summary>Gets the stored command name of a delivery row, computed from the type.</summary>
    internal static string CommandType { get; } =
        InternalCommandNameResolver.GetStorageName(typeof(DeliverWebhookInternalCommand));

    /// <summary>Builds the lane from the webhook settings; the receiver key is the subscription id.</summary>
    /// <param name="settings">The validated webhook settings.</param>
    /// <returns>The lane.</returns>
    internal static InternalCommandLane Create(WebhookSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new InternalCommandLane(
            LaneName,
            CommandType,
            TimeSpan.FromSeconds(settings.ReceiverCooldownSeconds),
            command => command is DeliverWebhookInternalCommand delivery
                ? delivery.SubscriptionId.ToString("N")
                : null);
    }

    /// <summary>
    /// Registers the lane registry and the lane hosted service when <c>Webhooks:Enabled</c> and
    /// <c>InternalCommands:Enabled</c> are both true, read from configuration here exactly as the
    /// queue reads its own flag. A second call registers nothing: the registry descriptor is the guard.
    /// </summary>
    /// <param name="services">The collection being configured.</param>
    /// <param name="configuration">Application configuration.</param>
    internal static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var webhooks = configuration.GetSection(WebhookSettings.SectionName).Get<WebhookSettings>() ?? new WebhookSettings();
        var queue = configuration.GetSection(InternalCommandsSettings.SectionName).Get<InternalCommandsSettings>()
            ?? new InternalCommandsSettings();

        if (!webhooks.Enabled || !queue.Enabled || services.Any(d => d.ServiceType == typeof(InternalCommandLanes)))
        {
            return;
        }

        services.AddSingleton(new InternalCommandLanes(CommandType));

        // A plain singleton with a factory: AddHostedService goes through TryAddEnumerable, which
        // refuses a factory registration of IHostedService.
        services.AddSingleton<IHostedService>(provider => new InternalCommandLaneProcessor(
            ActivatorUtilities.CreateInstance<InternalCommandProcessor>(provider),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ILogger<InternalCommandLaneProcessor>>(),
            provider.GetRequiredService<IInternalCommandSignal>(),
            provider.GetRequiredService<FrameworkTableTargets>(),
            provider.GetRequiredService<TimeProvider>(),
            Create(provider.GetRequiredService<IOptions<WebhookSettings>>().Value)));
    }
}
