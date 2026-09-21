using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMCA.Common.AI.Providers;

namespace MMCA.Common.AI.Anthropic;

/// <summary>
/// Registers the Anthropic provider so <c>AddMmcaChatClient(configuration)</c> can select it by
/// <c>Ai:Provider</c>. Registration is additive and idempotent: a host may register several
/// providers and pick one in configuration per environment.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class AnthropicAiServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the <see cref="AnthropicAiProviderFactory"/> to the providers
        /// <c>AddMmcaChatClient</c> chooses from.
        /// </summary>
        /// <returns>The same collection for chaining.</returns>
        public IServiceCollection AddAnthropicAiProvider()
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAiProviderFactory, AnthropicAiProviderFactory>());

            return services;
        }
    }
}
