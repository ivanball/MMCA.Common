using System.Diagnostics.CodeAnalysis;
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
    }
}
