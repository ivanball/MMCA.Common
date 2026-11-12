using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Infrastructure.Persistence.Webhooks;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>Registers outbound webhooks.</summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class WebhookServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers outbound webhooks: the <c>Webhooks</c> settings (validated at startup), the EF
        /// subscription store, the administration service, the target validator, the signed sender and
        /// its <c>MMCA.Webhooks</c> client, the data-protection secret protector, the DNS resolver, the
        /// event catalog and a startup notice for an ignored development switch.
        /// </summary>
        /// <param name="configuration">Application configuration for binding the settings section.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// Calling this alone turns nothing on: with <c>Webhooks:Enabled</c> false the fan-out stays inert,
        /// every administration endpoint answers 404 and no context maps the <c>WebhookSubscriptions</c>
        /// table. Turning it on also needs <c>pipeline.AddWebhookDelivery()</c> inside
        /// <c>AddMmcaApplicationPipeline</c>, one <c>AddWebhookEvent&lt;TEvent&gt;(name)</c> per published
        /// event, the migration on the <c>Webhooks:DataSourceName</c> database
        /// (<c>DesignTimeDbContextOptions.EnableWebhooks</c>), and a controller deriving from
        /// <c>WebhookSubscriptionsControllerBase</c> granted <c>webhooks:manage</c>.
        /// </para>
        /// <para>
        /// The <c>MMCA.Webhooks</c> client's primary handler follows no redirect, uses no proxy, keeps no
        /// cookies, and checks every resolved address at connect time, so a stored target cannot reach the
        /// host's own network even after its DNS answer changes. A multi-replica host needs the shared
        /// data-protection key ring (ADR-069) to read secrets another replica wrote.
        /// </para>
        /// <para>
        /// The client runs its own handler and nothing else: every additional handler a host default adds
        /// (a resilience pipeline, service discovery, any host-wide delegating handler) is cleared, so the
        /// internal-command queue is the only retry and the only failure policy; its default loggers are
        /// removed, so the client stack never logs the target URL, which often carries the receiver's
        /// token in its path; and it propagates no <c>traceparent</c>, <c>tracestate</c> or
        /// <c>baggage</c> header to the receiver. The HTTP client trace still records the URL path, so a
        /// host that exports traces to a shared backend filters the <c>MMCA.Webhooks</c> spans itself.
        /// </para>
        /// <para>
        /// Deliveries are internal-command rows drained by a dedicated delivery lane, never by the shared
        /// queue, so a slow or failing receiver cannot delay the other internal commands. The lane sends
        /// one delivery at a time per replica. A subscription whose attempt failed is cooled down on that
        /// replica for <c>Webhooks:ReceiverCooldownSeconds</c>; its deliveries that come due meanwhile are
        /// not sent and each spends one attempt, so they retry and dead-letter on the normal
        /// <c>InternalCommands</c> budget.
        /// </para>
        /// </remarks>
        public IServiceCollection AddWebhooks(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<WebhookSettings>()
                .Bind(configuration.GetSection(WebhookSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.TryAddScoped<IWebhookSubscriptionStore, EFWebhookSubscriptionStore>();
            services.TryAddScoped<IWebhookSubscriptionService, WebhookSubscriptionService>();
            services.TryAddScoped<IWebhookTargetValidator, DnsWebhookTargetValidator>();
            services.TryAddScoped<IWebhookSender, HttpWebhookSender>();
            services.TryAddSingleton<IWebhookSecretProtector, DataProtectionWebhookSecretProtector>();
            services.TryAddSingleton<IWebhookDnsResolver, SystemWebhookDnsResolver>();
            services.TryAddSingleton<WebhookEventCatalog>();
            services.TryAddSingleton(TimeProvider.System);

            services.AddDataProtection();

            services.AddHttpClient(HttpWebhookSender.ClientName, (provider, client) =>
                    client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<WebhookSettings>>().Value.TimeoutSeconds))
                .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    UseCookies = false,
                    ActivityHeadersPropagator = null,
                    ConnectCallback = WebhookConnectCallback.Create(
                        provider.GetRequiredService<IWebhookDnsResolver>(),
                        WebhookInsecureTargets.AreAllowed(
                            provider.GetRequiredService<IOptions<WebhookSettings>>().Value,
                            provider.GetService<IHostEnvironment>())),
                })
                .ConfigureAdditionalHttpMessageHandlers((handlers, _) => handlers.Clear())
                .RemoveAllLoggers();

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, InsecureWebhookTargetsNoticeService>());

            WebhookDeliveryLane.Register(services, configuration);

            return services;
        }
    }
}
