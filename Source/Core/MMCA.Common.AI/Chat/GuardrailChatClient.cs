using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Chat;

/// <summary>
/// Runs every registered <see cref="IChatGuardrail"/> over the request on the way out and over the
/// response on the way back, and throws <see cref="ChatGuardrailException"/> on the first block.
/// <para>
/// The layer is only in the pipeline when the application registered at least one guardrail, so a
/// host that adopts none keeps the chain it had. It sits inside the bounds (a call the configuration
/// forbids is refused before a guardrail is asked about it) and outside usage recording (a blocked
/// request never reaches the provider, so it has no cost to record).
/// </para>
/// </summary>
/// <remarks>
/// The streaming path inspects the REQUEST only. Inspecting a streamed answer would mean buffering it
/// to the end, which defeats the reason a caller chose streaming; an application that needs response
/// inspection on streamed output owns that decision and can buffer at its own call site.
/// </remarks>
public sealed class GuardrailChatClient : DelegatingChatClient
{
    private readonly IChatGuardrail[] _guardrails;

    /// <summary>Initializes a new instance of the <see cref="GuardrailChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="guardrails">Every guardrail to run, in registration order.</param>
    public GuardrailChatClient(IChatClient innerClient, IEnumerable<IChatGuardrail> guardrails)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(guardrails);
        _guardrails = [.. guardrails];
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialized once: the guardrails and the inner client must see the same messages, and a
        // caller is free to hand us a lazily generated sequence.
        var inspected = Materialize(messages);

        await InspectRequestAsync(inspected, options, cancellationToken).ConfigureAwait(false);

        var response = await base.GetResponseAsync(inspected, options, cancellationToken).ConfigureAwait(false);

        foreach (var guardrail in _guardrails)
        {
            var verdict = await guardrail
                .InspectResponseAsync(response, options, cancellationToken)
                .ConfigureAwait(false);

            if (!verdict.IsAllowed)
            {
                throw new ChatGuardrailException(verdict.Reason ?? GuardrailVerdict.UnspecifiedReason);
            }
        }

        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var inspected = Materialize(messages);

        await InspectRequestAsync(inspected, options, cancellationToken).ConfigureAwait(false);

        await foreach (var update in base.GetStreamingResponseAsync(inspected, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages as IReadOnlyList<ChatMessage> ?? [.. messages];
    }

    private async Task InspectRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        foreach (var guardrail in _guardrails)
        {
            var verdict = await guardrail
                .InspectRequestAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            if (!verdict.IsAllowed)
            {
                throw new ChatGuardrailException(verdict.Reason ?? GuardrailVerdict.UnspecifiedReason);
            }
        }
    }
}
