using System.Diagnostics.CodeAnalysis;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Observability;

namespace MMCA.Common.AI;

/// <summary>
/// The single composition entry point for the governed chat client.
/// <para>
/// <b>What it registers.</b> When <c>Ai:Enabled</c> is true, one <c>IChatClient</c> built as a
/// pipeline, outermost first:
/// </para>
/// <code>
/// BoundedChatClient          -- what the call is allowed to do (tokens, timeout, tools, input size)
///   UsageRecordingChatClient -- what the call cost (mmca.ai.input_tokens / output_tokens)
///     DistributedCaching     -- optional: Ai:EnableCache AND a registered IDistributedCache
///       OpenTelemetry        -- traces under the MMCA.Common.AI source
///         Logging
///           provider client  -- Anthropic via the SDK's own AsIChatClient adapter
/// </code>
/// <para>
/// The order is the point. Bounds are outermost so nothing downstream can be asked to do something
/// the configuration forbids, and so a rejected call is rejected before it is logged or cached.
/// Usage recording sits just inside them, which does mean a cache HIT records the usage of the
/// cached response: that is deliberate, and reads as "what this call would have cost" rather than
/// "what was billed" (the provider span is absent on a hit, so the two are distinguishable).
/// </para>
/// <para>
/// <b>What it registers when disabled: nothing.</b> A host with <c>Ai:Enabled</c> false gets the
/// bound-and-validated <see cref="AiSettings"/> and no <c>IChatClient</c> at all, so consuming code
/// gates on <c>GetService&lt;IChatClient&gt;()</c> returning <see langword="null"/> rather than on a
/// flag. A feature that cannot resolve a client is off by construction, which is the behavior a
/// module wants when the key is missing in a given environment.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class AiServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Binds <see cref="AiSettings"/> from the <c>Ai</c> configuration section and, when it is
        /// enabled, registers the governed <c>IChatClient</c> over the configured provider.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The same collection for chaining.</returns>
        public IServiceCollection AddMmcaChatClient(IConfiguration configuration) =>
            services.AddMmcaChatClient(configuration, CreateProviderChatClient);

        /// <summary>
        /// The provider-agnostic overload: same binding, same validation, same governance pipeline,
        /// with the innermost client supplied by the caller.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="providerFactory">Builds the innermost, ungoverned provider client.</param>
        /// <returns>The same collection for chaining.</returns>
        /// <remarks>
        /// This is the extension point a test uses to exercise the pipeline without a network, and
        /// the one a future provider (or a host with its own credential story) plugs into. The
        /// Anthropic overload above is nothing more than this one with a known factory.
        /// </remarks>
        public IServiceCollection AddMmcaChatClient(
            IConfiguration configuration,
            Func<IServiceProvider, IChatClient> providerFactory)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(providerFactory);

            var section = configuration.GetSection(AiSettings.SectionName);

            services.AddOptions<AiSettings>()
                .Bind(section)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            var settings = section.Get<AiSettings>() ?? new AiSettings();
            if (!settings.Enabled)
            {
                return services;
            }

            services.AddMetrics();
            services.TryAddSingleton<AiUsageMeter>();

            // First registered is outermost (ChatClientBuilder applies its factories in reverse), so
            // this list reads exactly like the diagram in the type remarks.
            var builder = services
                .AddChatClient(providerFactory)
                .Use((inner, serviceProvider) => new BoundedChatClient(
                    inner,
                    serviceProvider.GetRequiredService<IOptions<AiSettings>>().Value))
                .Use((inner, serviceProvider) => new UsageRecordingChatClient(
                    inner,
                    serviceProvider.GetRequiredService<AiUsageMeter>(),
                    serviceProvider.GetRequiredService<IOptions<AiSettings>>().Value.Provider));

            // A cache the host never registered would make every call throw at resolve time, so the
            // opt-in needs both halves: the switch AND a store to write to.
            if (settings.EnableCache && services.Any(descriptor => descriptor.ServiceType == typeof(IDistributedCache)))
            {
                builder = builder.UseDistributedCache();
            }

            var enableSensitiveData = IsDevelopmentHost(services);

            builder
                .UseOpenTelemetry(
                    sourceName: AiUsageMeter.MeterName,
                    configure: client => client.EnableSensitiveData = enableSensitiveData)
                .UseLogging();

            return services;
        }
    }

    /// <summary>
    /// Builds the innermost provider client for <see cref="AiSettings.Provider"/>.
    /// </summary>
    /// <param name="serviceProvider">The resolved service provider.</param>
    /// <returns>An ungoverned provider client, which the caller then wraps.</returns>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership passes to the IChatClient the adapter returns, which the pipeline wraps and the container disposes with the singleton. Disposing here would close the transport before the first call.")]
    private static IChatClient CreateProviderChatClient(IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<IOptions<AiSettings>>().Value;

        return settings.Provider switch
        {
            // AsIChatClient is the official SDK's own Microsoft.Extensions.AI adapter
            // (Microsoft.Extensions.AI.AnthropicClientExtensions), so nothing here hand-rolls the
            // Messages API over HttpClient. The model and the output ceiling are passed as the
            // client's defaults; BoundedChatClient still clamps per call, because a default is a
            // suggestion and a bound is not.
            AiProvider.Anthropic => new AnthropicClient(new ClientOptions
            {
                ApiKey = settings.ApiKey,
                Timeout = settings.Timeout,
            }).AsIChatClient(settings.Model, settings.MaxOutputTokens),
            _ => throw new NotSupportedException(
                $"{AiSettings.SectionName}:{nameof(AiSettings.Provider)} '{settings.Provider}' has no built-in "
                + "client. Supply one through the AddMmcaChatClient(IConfiguration, Func<IServiceProvider, IChatClient>) overload."),
        };
    }

    /// <summary>
    /// Answers whether this host is Development, by reading the environment instance the host
    /// builder already placed in the collection.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <returns><see langword="true"/> only when a Development environment is positively identified.</returns>
    /// <remarks>
    /// Fails CLOSED: an environment it cannot see reads as not-Development, so prompt and completion
    /// text never reach telemetry by accident. Mirrors the gate on
    /// <c>Persistence:EnableSensitiveDataLogging</c>, where the same rule keeps a debugging aid from
    /// becoming a production data leak.
    /// </remarks>
    private static bool IsDevelopmentHost(IServiceCollection services) =>
        services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IHostEnvironment>()
            .Any(environment => environment.IsDevelopment());
}
