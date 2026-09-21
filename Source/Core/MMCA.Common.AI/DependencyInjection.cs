using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.AI.Chat;
using MMCA.Common.AI.Guardrails;
using MMCA.Common.AI.Observability;
using MMCA.Common.AI.Providers;

namespace MMCA.Common.AI;

/// <summary>
/// The single composition entry point for the governed chat client.
/// <para>
/// <b>What it registers.</b> When <c>Ai:Enabled</c> is true, one <c>IChatClient</c> built as a
/// pipeline, outermost first:
/// </para>
/// <code>
/// BoundedChatClient            -- what the call is allowed to do (tokens, timeout, tools, input size, model)
///   GuardrailChatClient        -- optional: only when an IChatGuardrail is registered
///     UsageRecordingChatClient -- what the call cost and how long it took
///       DistributedCaching     -- optional: Ai:EnableCache AND a registered IDistributedCache
///         OpenTelemetry        -- traces under the MMCA.Common.AI source
///           PromptTagging      -- the prompt identity as attributes on that trace
///             Logging
///               provider client -- whichever IAiProviderFactory matches Ai:Provider
/// </code>
/// <para>
/// The order is the point. Bounds are outermost so nothing downstream can be asked to do something
/// the configuration forbids, and so a rejected call is rejected before it is logged or cached.
/// Guardrails sit just inside the bounds and outside usage recording: a blocked request never
/// reaches the provider, so it has no cost to record. Usage recording sits inside them, which does
/// mean a cache HIT records the usage of the cached response: that is deliberate, and reads as
/// "what this call would have cost" rather than "what was billed" (the provider span is absent on a
/// hit, so the two are distinguishable). Prompt tagging sits just inside the OpenTelemetry layer so
/// the activity it decorates is that layer's own <c>gen_ai</c> span.
/// </para>
/// <para>
/// The guardrail layer is added ONLY when the host registered at least one
/// <see cref="Chat.IChatGuardrail"/> or one <see cref="Guardrails.IChatRequestRedactor"/>, so an
/// application that adopts neither keeps the chain it had, down to the type the container hands
/// back. Registering neither is itself a startup failure while
/// <see cref="AiSettings.RequireGuardrail"/> is true (its default), and
/// <c>AddPiiRedactionGuardrail()</c> is the one-line answer.
/// </para>
/// <para>
/// <b>Two registration-time refusals.</b> Both are thrown here rather than left to the first
/// request, because a bound nobody reaches is not a bound: an application that ships without a
/// guardrail, or with tools enabled and no policy to gate them, should fail the deployment rather
/// than the tenth user.
/// </para>
/// <para>
/// <b>The provider is a registered factory, never a type this package names.</b> The
/// configuration overload resolves every <see cref="IAiProviderFactory"/> the host registered
/// (one per adapter package: <c>AddAnthropicAiProvider()</c>, <c>AddOpenAiProvider()</c>) and
/// selects the one whose <see cref="IAiProviderFactory.Name"/> matches <c>Ai:Provider</c>. That
/// match is validated at startup through the options pipeline, so a host naming a provider it
/// never registered fails at boot with the registered names in the message, not on a user's
/// first request.
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
        /// enabled, registers the governed <c>IChatClient</c> over the provider whose registered
        /// <see cref="IAiProviderFactory"/> matches <c>Ai:Provider</c>.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The same collection for chaining.</returns>
        public IServiceCollection AddMmcaChatClient(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);

            // Registered before the shared path so ValidateOnStart runs it with the data
            // annotations: an unknown or unregistered provider is a boot failure, not a first-call one.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AiSettings>, AiProviderValidator>());

            return services.AddMmcaChatClient(configuration, CreateProviderChatClient);
        }

        /// <summary>
        /// The factory overload: same binding, same validation, same governance pipeline, with the
        /// innermost client supplied by the caller instead of a registered provider factory.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="providerFactory">Builds the innermost, ungoverned provider client.</param>
        /// <returns>The same collection for chaining.</returns>
        /// <remarks>
        /// This is the extension point a test uses to exercise the pipeline without a network, and
        /// the one a host with its own credential story plugs into. The configuration overload is
        /// nothing more than this one with the registered <see cref="IAiProviderFactory"/> as the
        /// factory. <c>Ai:Provider</c> is still required here (it names what the metrics fall back
        /// to when the client reports no provider of its own) but is not matched against anything.
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

            // The descriptor checks (not resolves) are what keep the guardrail layer out of a host
            // that registered neither: adding an empty-loop client would change the type the
            // container hands back for every application that adopts none of this. They are also
            // what the two registration-time refusals below read, so the answer is the same one the
            // pipeline is built from.
            var hasGuardrail = services.Any(descriptor => descriptor.ServiceType == typeof(IChatGuardrail));
            var hasRedactor = services.Any(descriptor => descriptor.ServiceType == typeof(IChatRequestRedactor));

            RefuseAnUngovernedHost(services, settings, hasGuardrail, hasRedactor);

            services.AddMetrics();
            services.TryAddSingleton<AiUsageMeter>();

            // First registered is outermost (ChatClientBuilder applies its factories in reverse), so
            // this list reads exactly like the diagram in the type remarks.
            var builder = services
                .AddChatClient(providerFactory)
                .Use((inner, serviceProvider) => new BoundedChatClient(
                    inner,
                    serviceProvider.GetRequiredService<IOptions<AiSettings>>().Value,
                    serviceProvider.GetServices<IChatToolPolicy>()));

            if (hasGuardrail || hasRedactor)
            {
                builder = builder.Use((inner, serviceProvider) => new GuardrailChatClient(
                    inner,
                    serviceProvider.GetServices<IChatGuardrail>(),
                    serviceProvider.GetServices<IChatRequestRedactor>()));
            }

            builder = builder
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
                .Use(inner => new PromptTaggingChatClient(inner))
                .UseLogging();

            return services;
        }
    }

    /// <summary>
    /// Throws when an enabled host has switched something on without registering what governs it.
    /// </summary>
    /// <param name="services">The service collection being configured.</param>
    /// <param name="settings">The bound settings for this host.</param>
    /// <param name="hasGuardrail">Whether an <see cref="IChatGuardrail"/> descriptor is present.</param>
    /// <param name="hasRedactor">Whether an <see cref="IChatRequestRedactor"/> descriptor is present.</param>
    /// <exception cref="InvalidOperationException">When a required extension point is missing.</exception>
    /// <remarks>
    /// At registration rather than on the first call, and with the fix in the message both times: a
    /// bound nobody reaches is not a bound, so the deployment fails instead of the tenth user.
    /// </remarks>
    private static void RefuseAnUngovernedHost(
        IServiceCollection services,
        AiSettings settings,
        bool hasGuardrail,
        bool hasRedactor)
    {
        if (settings.RequireGuardrail && !hasGuardrail && !hasRedactor)
        {
            throw new InvalidOperationException(
                $"{AiSettings.SectionName}:{nameof(AiSettings.Enabled)} is true but this host registered no "
                + $"{nameof(IChatGuardrail)} and no {nameof(IChatRequestRedactor)}, so nothing inspects what is "
                + "sent to the model or what comes back. Register a guardrail before AddMmcaChatClient, or call "
                + "AddPiiRedactionGuardrail() for the one this framework ships. A host that deliberately wants "
                + $"none sets {AiSettings.SectionName}:{nameof(AiSettings.RequireGuardrail)} to false, which is a "
                + "reviewable line rather than an absence nobody can see.");
        }

        if (settings.AllowTools && !services.Any(descriptor => descriptor.ServiceType == typeof(IChatToolPolicy)))
        {
            throw new InvalidOperationException(
                $"{AiSettings.SectionName}:{nameof(AiSettings.AllowTools)} is true but this host registered no "
                + $"{nameof(IChatToolPolicy)}, so nothing decides which tools the model may be offered and every "
                + $"tool would be stripped. Register an {nameof(IChatToolPolicy)} before AddMmcaChatClient, or "
                + $"leave {AiSettings.SectionName}:{nameof(AiSettings.AllowTools)} false.");
        }
    }

    /// <summary>
    /// Builds the innermost provider client by resolving the registered
    /// <see cref="IAiProviderFactory"/> whose name matches <see cref="AiSettings.Provider"/>.
    /// </summary>
    /// <param name="serviceProvider">The resolved service provider.</param>
    /// <returns>An ungoverned provider client, which the caller then wraps.</returns>
    /// <remarks>
    /// <see cref="AiProviderValidator"/> has already refused a name with no factory at startup, so
    /// the throw here is a belt for the case where the options pipeline was bypassed.
    /// </remarks>
    private static IChatClient CreateProviderChatClient(IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<IOptions<AiSettings>>().Value;
        var factories = serviceProvider.GetServices<IAiProviderFactory>().ToArray();

        var factory = AiProviderValidator.Match(factories, settings.Provider)
            ?? throw new InvalidOperationException(AiProviderValidator.DescribeMismatch(factories, settings.Provider));

        return factory.Create(settings, serviceProvider);
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
