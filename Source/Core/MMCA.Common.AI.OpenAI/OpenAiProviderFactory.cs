using System.ClientModel;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Providers;
using OpenAI;

namespace MMCA.Common.AI.OpenAI;

/// <summary>
/// The OpenAI half of the provider boundary: builds the official SDK's chat client for the
/// configured model, key, endpoint and timeout, adapted to <see cref="IChatClient"/> by
/// Microsoft.Extensions.AI.OpenAI, and nothing else. Selected by <c>Ai:Provider</c> =
/// <see cref="ProviderName"/> once <c>AddOpenAiProvider()</c> registered it.
/// <para>
/// <see cref="AiSettings.Endpoint"/> is what points this adapter at an OpenAI-compatible server or
/// an AI gateway; left unset, the SDK's public endpoint applies.
/// </para>
/// </summary>
public sealed class OpenAiProviderFactory : IAiProviderFactory
{
    /// <summary>The name a configuration file selects this provider by.</summary>
    public const string ProviderName = "OpenAI";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    /// <remarks>
    /// The OpenAI client binds its model at construction, so the pinned model is the one every
    /// request goes to; <c>BoundedChatClient</c> refuses a request naming any other, which is what
    /// keeps a prompt contract's model meaningful here as well as on adapters that honor a
    /// per-request override.
    /// </remarks>
    public IChatClient Create(AiSettings settings, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var model = settings.Model
            ?? throw new InvalidOperationException($"{AiSettings.SectionName}:{nameof(AiSettings.Model)} is required.");
        var apiKey = settings.ApiKey
            ?? throw new InvalidOperationException($"{AiSettings.SectionName}:{nameof(AiSettings.ApiKey)} is required.");

        var options = new OpenAIClientOptions { NetworkTimeout = settings.Timeout };

        if (settings.Endpoint is { } endpoint)
        {
            options.Endpoint = endpoint;
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options)
            .GetChatClient(model)
            .AsIChatClient();
    }
}
