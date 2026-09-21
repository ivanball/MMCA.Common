using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// The <c>provider</c> tag is read from the inner client itself: the <see cref="ChatClientMetadata"/>
/// every Microsoft.Extensions.AI adapter publishes carries the provider's own name
/// (<c>anthropic</c>, <c>openai</c>), so a host that supplied a foreign client through the factory
/// overload is metered as what it is. The configured <see cref="AiSettings.Provider"/> is only the
/// fallback for a client that reports none, lower-cased to match the convention the adapters use.
/// </para>
/// <para>
/// The streaming path accumulates usage from the update stream, where providers deliver it as a
/// <see cref="UsageContent"/> item (typically on the final update), so a streamed answer is counted
/// exactly like a buffered one.
/// </para>
/// <para>
/// It also records end-to-end latency on <see cref="AiUsageMeter.CallDurationHistogramName"/>, with
/// an <c>outcome</c> of success, error or canceled. Microsoft.Extensions.AI's own
/// <c>UseOpenTelemetry</c> layer already publishes <c>gen_ai.client.operation.duration</c> on the
/// same meter; this histogram is not a duplicate of it but the same measurement carried under this
/// framework's attribution dimensions (<c>prompt_name</c>, <c>prompt_version</c>) plus the outcome,
/// which is what lets one query answer "which prompt got slower, and how often does it fail".
/// </para>
/// </summary>
public sealed class UsageRecordingChatClient : DelegatingChatClient
{
    private readonly AiUsageMeter _meter;

    /// <summary>Initializes a new instance of the <see cref="UsageRecordingChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="meter">The meter to report to.</param>
    /// <param name="configuredProvider">The configured provider name, used only when the inner client reports none.</param>
    public UsageRecordingChatClient(IChatClient innerClient, AiUsageMeter meter, string? configuredProvider)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
        Provider = ResolveProviderName(innerClient, configuredProvider);
    }

    /// <summary>The provider name every measurement from this client is tagged with.</summary>
    public string Provider { get; }

    /// <summary>
    /// Resolves the provider tag for a client: what the client says it is, else what the host
    /// configured, else <c>unknown</c>.
    /// </summary>
    /// <param name="client">The client whose metadata to read.</param>
    /// <param name="configuredProvider">The configured fallback.</param>
    /// <returns>A non-empty provider name.</returns>
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The tag is a lowercase identifier matching the provider names Microsoft.Extensions.AI adapters report (anthropic, openai), not display text; upper-casing would split one provider into two series.")]
    public static string ResolveProviderName(IChatClient client, string? configuredProvider)
    {
        ArgumentNullException.ThrowIfNull(client);

        var reported = client.GetService<ChatClientMetadata>()?.ProviderName;
        if (!string.IsNullOrWhiteSpace(reported))
        {
            return reported;
        }

        return string.IsNullOrWhiteSpace(configuredProvider)
            ? "unknown"
            : configuredProvider.ToLowerInvariant();
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        ChatResponse response;

        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RecordDuration(startedAt, options?.ModelId, options, AiUsageMeter.CanceledOutcome);
            throw;
        }
        catch (Exception)
        {
            RecordDuration(startedAt, options?.ModelId, options, AiUsageMeter.ErrorOutcome);
            throw;
        }

        RecordDuration(startedAt, response.ModelId ?? options?.ModelId, options, AiUsageMeter.SuccessOutcome);

        _meter.Record(
            response.Usage,
            response.ModelId ?? options?.ModelId,
            PromptContract.ReadName(options),
            PromptContract.ReadVersion(options),
            Provider);

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        string? modelId = null;
        var outcome = AiUsageMeter.SuccessOutcome;

        // Hand-driven rather than `await foreach`, because the duration has to be attributed to the
        // outcome and C# forbids a `yield return` inside a try that has a catch clause. The stream
        // ENDING is what stops the clock, which is the number a caller of a streaming API feels.
        var updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool moved;

                try
                {
                    moved = await updates.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = AiUsageMeter.CanceledOutcome;
                    throw;
                }
                catch (Exception)
                {
                    outcome = AiUsageMeter.ErrorOutcome;
                    throw;
                }

                if (!moved)
                {
                    break;
                }

                var update = updates.Current;
                modelId ??= update.ModelId;
                RecordUsageIn(update, modelId ?? options?.ModelId, options);

                yield return update;
            }
        }
        finally
        {
            RecordDuration(startedAt, modelId ?? options?.ModelId, options, outcome);
            await updates.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports any usage a streamed update carried. Providers deliver it as a
    /// <see cref="UsageContent"/> item, typically on the final update.
    /// </summary>
    private void RecordUsageIn(ChatResponseUpdate update, string? model, ChatOptions? options)
    {
        foreach (var usage in update.Contents.OfType<UsageContent>())
        {
            _meter.Record(
                usage.Details,
                model,
                PromptContract.ReadName(options),
                PromptContract.ReadVersion(options),
                Provider);
        }
    }

    /// <summary>
    /// Stops the clock and reports the elapsed time under this call's attribution dimensions.
    /// </summary>
    private void RecordDuration(long startedAt, string? model, ChatOptions? options, string outcome) =>
        _meter.RecordDuration(
            Stopwatch.GetElapsedTime(startedAt),
            model,
            PromptContract.ReadName(options),
            PromptContract.ReadVersion(options),
            Provider,
            outcome);
}
