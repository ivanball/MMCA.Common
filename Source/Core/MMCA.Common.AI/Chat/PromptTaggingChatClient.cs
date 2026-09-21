using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Observability;

namespace MMCA.Common.AI.Chat;

/// <summary>
/// Copies the prompt identity a <see cref="PromptContract"/> stamped on the request
/// (<c>mmca.prompt.name</c>, <c>mmca.prompt.version</c>, <c>mmca.prompt.hash</c>) onto the current
/// activity, so a trace can be filtered by the prompt that produced it exactly as the token
/// counters already can.
/// <para>
/// It is composed just INSIDE the pipeline's OpenTelemetry layer, so when that layer has a
/// listener the current activity is its own <c>gen_ai</c> span and the tags land there; and it
/// only ever tags an activity from the <see cref="AiUsageMeter.MeterName"/> source, so a host with
/// no listener for that source gets no stray tags on its HTTP request span. Nothing is written
/// when the request carries no contract.
/// </para>
/// </summary>
public sealed class PromptTaggingChatClient : DelegatingChatClient
{
    /// <summary>Initializes a new instance of the <see cref="PromptTaggingChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    public PromptTaggingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Tag(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Tag(options);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static void Tag(ChatOptions? options)
    {
        if (Activity.Current is not { } activity
            || !string.Equals(activity.Source.Name, AiUsageMeter.MeterName, StringComparison.Ordinal)
            || options?.AdditionalProperties is not { } properties)
        {
            return;
        }

        foreach (var key in (ReadOnlySpan<string>)[PromptContract.NamePropertyKey, PromptContract.VersionPropertyKey, PromptContract.HashPropertyKey])
        {
            if (properties.TryGetValue(key, out var value) && value is string text)
            {
                activity.SetTag(key, text);
            }
        }
    }
}
