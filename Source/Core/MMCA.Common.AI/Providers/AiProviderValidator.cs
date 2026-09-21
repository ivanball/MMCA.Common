using Microsoft.Extensions.Options;

namespace MMCA.Common.AI.Providers;

/// <summary>
/// The startup half of provider selection: refuses an enabled <c>Ai</c> section whose
/// <see cref="AiSettings.Provider"/> matches no registered <see cref="IAiProviderFactory"/>.
/// <para>
/// Registered by the configuration overload of <c>AddMmcaChatClient</c> only, and run by
/// <c>ValidateOnStart</c> alongside the data annotations, so a host that names a provider it never
/// registered (or registered none at all) fails at boot with the fix in the message. The factory
/// overload does not register it, because there the caller supplied the client and the name is
/// only a metrics label.
/// </para>
/// </summary>
internal sealed class AiProviderValidator(IEnumerable<IAiProviderFactory> factories) : IValidateOptions<AiSettings>
{
    private readonly IAiProviderFactory[] _factories = [.. factories];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AiSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Provider is required when Enabled by the data annotations; a blank here is reported once,
        // there, rather than twice.
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Provider))
        {
            return ValidateOptionsResult.Success;
        }

        return Match(_factories, options.Provider) is not null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(DescribeMismatch(_factories, options.Provider));
    }

    /// <summary>Finds the factory whose name matches, ignoring case.</summary>
    /// <param name="factories">The registered factories.</param>
    /// <param name="provider">The configured provider name.</param>
    /// <returns>The matching factory, or <see langword="null"/>.</returns>
    internal static IAiProviderFactory? Match(IReadOnlyList<IAiProviderFactory> factories, string? provider) =>
        factories.FirstOrDefault(factory => string.Equals(factory.Name, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the message for a provider name with no factory behind it.</summary>
    /// <param name="factories">The registered factories.</param>
    /// <param name="provider">The configured provider name.</param>
    /// <returns>A message naming the registered providers, or the adapter packages when there are none.</returns>
    internal static string DescribeMismatch(IReadOnlyList<IAiProviderFactory> factories, string? provider)
    {
        var setting = $"{AiSettings.SectionName}:{nameof(AiSettings.Provider)}";

        if (factories.Count == 0)
        {
            return $"{setting} is '{provider}' but no {nameof(IAiProviderFactory)} is registered. Reference an "
                + "adapter package and register its provider (MMCA.Common.AI.Anthropic: AddAnthropicAiProvider(); "
                + "MMCA.Common.AI.OpenAI: AddOpenAiProvider()), or supply the client through the "
                + "AddMmcaChatClient(IConfiguration, Func<IServiceProvider, IChatClient>) overload.";
        }

        var registered = string.Join(", ", factories.Select(factory => factory.Name).Order(StringComparer.OrdinalIgnoreCase));

        return $"{setting} is '{provider}' but the registered providers are: {registered}. Set the value to one of "
            + "them, or reference and register the adapter package for the provider you meant.";
    }
}
