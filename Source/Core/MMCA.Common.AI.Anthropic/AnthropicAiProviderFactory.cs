using System.Diagnostics.CodeAnalysis;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Providers;

namespace MMCA.Common.AI.Anthropic;

/// <summary>
/// The Anthropic half of the provider boundary: builds the official SDK's Microsoft.Extensions.AI
/// client for the configured model, key, endpoint and timeout, and nothing else. Selected by
/// <c>Ai:Provider</c> = <see cref="ProviderName"/> once <c>AddAnthropicAiProvider()</c> registered it.
/// </summary>
public sealed class AnthropicAiProviderFactory : IAiProviderFactory
{
    /// <summary>The name a configuration file selects this provider by.</summary>
    public const string ProviderName = "Anthropic";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    /// <remarks>
    /// <c>AsIChatClient</c> is the SDK's own Microsoft.Extensions.AI adapter, so nothing here
    /// hand-rolls the Messages API over <c>HttpClient</c>. The model and the output ceiling are
    /// passed as the client's defaults; <c>BoundedChatClient</c> still pins and clamps per call,
    /// because a default is a suggestion and a bound is not.
    /// </remarks>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership passes to the IChatClient the adapter returns, which the governed pipeline wraps and the container disposes with the singleton. Disposing here would close the transport before the first call.")]
    public IChatClient Create(AiSettings settings, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var options = new ClientOptions
        {
            ApiKey = settings.ApiKey,
            Timeout = settings.Timeout,
        };

        if (settings.Endpoint is { } endpoint)
        {
            options.BaseUrl = endpoint.ToString();
        }

        return new AnthropicClient(options).AsIChatClient(settings.Model, settings.MaxOutputTokens);
    }
}
