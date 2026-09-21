using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.AI.Chat;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// Registration for the guardrails this package ships, as opposed to the ones an application
/// writes for itself.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GuardrailServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="PiiRedactionGuardrail"/> as both an <see cref="IChatRequestRedactor"/>
        /// and an <see cref="IChatGuardrail"/>, so contact details are stripped from every outgoing
        /// message and the host satisfies <see cref="AiSettings.RequireGuardrail"/>.
        /// </summary>
        /// <returns>The same collection for chaining.</returns>
        /// <remarks>
        /// One singleton resolved under both contracts, not two instances: the redactor and the
        /// guardrail are two faces of one decision, and a host that later gives the type state would
        /// otherwise get two copies of it. Call it before <c>AddMmcaChatClient</c>; that method reads
        /// the descriptors to decide whether to compose the guardrail layer at all.
        /// </remarks>
        public IServiceCollection AddPiiRedactionGuardrail()
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<PiiRedactionGuardrail>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatGuardrail, PiiRedactionGuardrail>(
                serviceProvider => serviceProvider.GetRequiredService<PiiRedactionGuardrail>()));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatRequestRedactor, PiiRedactionGuardrail>(
                serviceProvider => serviceProvider.GetRequiredService<PiiRedactionGuardrail>()));

            return services;
        }

        /// <summary>
        /// Binds <see cref="ContentPolicySettings"/> from the <c>Ai:ContentPolicy</c> section and
        /// registers <see cref="ContentPolicyGuardrail"/> as both an
        /// <see cref="IChatRequestRedactor"/> and an <see cref="IChatGuardrail"/>, so
        /// prompt-injection markers in user content and answers matching a configured pattern are
        /// handled on every call and the host satisfies <see cref="AiSettings.RequireGuardrail"/>.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <returns>The same collection for chaining.</returns>
        /// <remarks>
        /// Validated on start, so a pattern that does not compile fails the deployment naming the
        /// offending pattern rather than throwing on a user's request. One singleton under both
        /// contracts, for the same reason <c>AddPiiRedactionGuardrail</c> registers one. Call it
        /// before <c>AddMmcaChatClient</c>; that method reads the descriptors to decide whether to
        /// compose the guardrail layer at all.
        /// </remarks>
        public IServiceCollection AddContentPolicyGuardrail(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddOptions<ContentPolicySettings>()
                .Bind(configuration.GetSection(ContentPolicySettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.TryAddSingleton<ContentPolicyGuardrail>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatGuardrail, ContentPolicyGuardrail>(
                serviceProvider => serviceProvider.GetRequiredService<ContentPolicyGuardrail>()));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatRequestRedactor, ContentPolicyGuardrail>(
                serviceProvider => serviceProvider.GetRequiredService<ContentPolicyGuardrail>()));

            return services;
        }
    }
}
