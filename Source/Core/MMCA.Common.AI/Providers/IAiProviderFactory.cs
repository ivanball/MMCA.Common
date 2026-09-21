using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Providers;

/// <summary>
/// Builds the innermost, ungoverned <see cref="IChatClient"/> for one language-model provider.
/// <para>
/// This is the whole provider boundary. The governance pipeline (bounds, guardrails, usage
/// metering, telemetry) never sees a vendor type; it wraps whatever client the factory whose
/// <see cref="Name"/> matches <see cref="AiSettings.Provider"/> hands back. Each adapter package
/// (<c>MMCA.Common.AI.Anthropic</c>, <c>MMCA.Common.AI.OpenAI</c>) ships exactly one implementation
/// and one registration method, so swapping the provider is a package reference, a registration
/// call and a configuration value, with nothing else in the host changing.
/// </para>
/// <para>
/// A host with a credential story no adapter covers bypasses this contract through the factory
/// overload of <c>AddMmcaChatClient</c> instead; the pipeline is the same either way.
/// </para>
/// </summary>
public interface IAiProviderFactory
{
    /// <summary>
    /// The provider name a configuration file selects this factory by (<c>Ai:Provider</c>), compared
    /// case-insensitively. Stable and vendor-shaped, e.g. <c>Anthropic</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Builds the provider client for the given, already validated, settings.
    /// </summary>
    /// <param name="settings">The bound <c>Ai</c> section, with <see cref="AiSettings.Enabled"/> true.</param>
    /// <param name="serviceProvider">The host's service provider, for adapters that need a logger or an <c>HttpClient</c> factory.</param>
    /// <returns>An ungoverned client, which the caller wraps. Ownership passes to the pipeline.</returns>
    IChatClient Create(AiSettings settings, IServiceProvider serviceProvider);
}
