using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Observability;

namespace MMCA.Common.AI.Chat;

/// <summary>
/// Reports every call's provider-reported token usage to <see cref="AiUsageMeter"/>, tagged with the
/// model and with the prompt identity <see cref="PromptContract"/> stamped on the request.
/// <para>
/// It reads the numbers the provider returned rather than estimating them, which is why it sits here
/// and not beside the input-budget check in <see cref="BoundedChatClient"/>: a bound has to be
/// decided BEFORE the call, and a cost has to be measured AFTER it.
/// </para>
/// <para>
/// The streaming path accumulates usage from the update stream, where providers deliver it as a
/// <see cref="UsageContent"/> item (typically on the final update), so a streamed answer is counted
/// exactly like a buffered one.
/// </para>
/// </summary>
public sealed class UsageRecordingChatClient : DelegatingChatClient
{
    private readonly AiUsageMeter _meter;
    private readonly AiProvider _provider;

    /// <summary>Initializes a new instance of the <see cref="UsageRecordingChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="meter">The meter to report to.</param>
    /// <param name="provider">The provider tag applied to every measurement.</param>
    public UsageRecordingChatClient(IChatClient innerClient, AiUsageMeter meter, AiProvider provider)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
        _provider = provider;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        _meter.Record(
            response.Usage,
            response.ModelId ?? options?.ModelId,
            PromptContract.ReadName(options),
            PromptContract.ReadVersion(options),
            _provider);

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? modelId = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            modelId ??= update.ModelId;

            foreach (var content in update.Contents)
            {
                if (content is UsageContent usage)
                {
                    _meter.Record(
                        usage.Details,
                        modelId ?? options?.ModelId,
                        PromptContract.ReadName(options),
                        PromptContract.ReadVersion(options),
                        _provider);
                }
            }

            yield return update;
        }
    }
}
