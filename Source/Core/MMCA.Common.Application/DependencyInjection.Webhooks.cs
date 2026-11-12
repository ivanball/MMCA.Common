using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application;

public static partial class DependencyInjection
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Publishes integration event <typeparamref name="TEvent"/> as outbound webhook type
        /// <paramref name="eventType"/>: registers the singleton fan-out handler and one catalog entry.
        /// </summary>
        /// <typeparam name="TEvent">
        /// The TERMINAL integration event contract (the dispatcher upcasts before handlers run, so a
        /// retired contract never reaches the fan-out). The WHOLE event is the published payload, so
        /// register only contracts you are willing to send to a third party.
        /// </typeparam>
        /// <param name="eventType">
        /// The webhook type name subscribers list: lower case, dot, dash or underscore separated, at most
        /// 100 characters (for example <c>ticket.opened</c>).
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="ArgumentException"><paramref name="eventType"/> is blank, <c>"*"</c> or malformed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The name is already registered for another type, or the type under another name.
        /// </exception>
        /// <remarks>
        /// Inert in a host that never calls <c>AddWebhooks(configuration)</c> or leaves
        /// <c>Webhooks:Enabled</c> false. Registering the same type under the same name twice is a no-op.
        /// Under broker messaging the fan-out runs only in a host that consumes the event, so a
        /// delivering host also registers the broker consumer for each webhook event.
        /// </remarks>
        public IServiceCollection AddWebhookEvent<TEvent>(string eventType)
            where TEvent : class, IIntegrationEvent
        {
            if (!WebhookEventCatalog.IsValidEventTypeName(eventType))
            {
                throw new ArgumentException(
                    $"'{eventType}' is not a valid webhook event type: use 1 to {WebhookEventCatalog.MaxEventTypeLength} lower-case letters and digits, "
                    + "separated by single dots, dashes or underscores, starting with a letter (for example ticket.opened).",
                    nameof(eventType));
            }

            foreach (var existing in services
                .Where(descriptor => descriptor.ServiceType == typeof(WebhookEventRegistration))
                .Select(descriptor => descriptor.ImplementationInstance)
                .OfType<WebhookEventRegistration>())
            {
                var sameType = existing.EventClrType == typeof(TEvent);
                var sameName = string.Equals(existing.EventType, eventType, StringComparison.Ordinal);
                if (sameType && sameName)
                {
                    return services;
                }

                if (sameName)
                {
                    throw new InvalidOperationException(
                        $"Webhook event type '{eventType}' is already registered for {existing.EventClrType.FullName}; "
                        + $"it cannot also be used for {typeof(TEvent).FullName}.");
                }

                if (sameType)
                {
                    throw new InvalidOperationException(
                        $"{typeof(TEvent).FullName} is already published as webhook event type '{existing.EventType}'; "
                        + $"it cannot also be published as '{eventType}'.");
                }
            }

            services.AddSingleton(new WebhookEventRegistration(typeof(TEvent), eventType));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IIntegrationEventHandler<TEvent>, WebhookFanOutHandler<TEvent>>());
            services.TryAddSingleton<WebhookEventCatalog>();
            return services;
        }
    }

    extension(MmcaApplicationPipelineBuilder pipeline)
    {
        /// <summary>
        /// Registers the webhook delivery handler inside the application pipeline, so every decorator
        /// wraps it, plus a startup check that fails, naming <c>AddWebhooks(configuration)</c>, when the
        /// host registered no subscription store.
        /// </summary>
        /// <returns>The pipeline builder for chaining.</returns>
        public MmcaApplicationPipelineBuilder AddWebhookDelivery()
        {
            var services = pipeline.Services;
            services.TryAddScoped<ICommandHandler<DeliverWebhookInternalCommand, Result>, DeliverWebhookInternalCommandHandler>();
            services.AddOptions<WebhookSettings>().ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebhookSettings>, WebhookRegistrationValidator>());
            services.TryAddSingleton<WebhookEventCatalog>();
            return pipeline;
        }
    }
}
