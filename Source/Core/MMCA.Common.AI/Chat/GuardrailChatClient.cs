using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Guardrails;

namespace MMCA.Common.AI.Chat;

/// <summary>
/// Redacts the request with every registered <see cref="IChatRequestRedactor"/>, then runs every
/// registered <see cref="IChatGuardrail"/> over the redacted request on the way out and over the
/// answer on the way back, and throws <see cref="ChatGuardrailException"/> on the first block.
/// <para>
/// The layer is only in the pipeline when the application registered at least one guardrail or one
/// redactor, so a host that adopts neither keeps the chain it had. It sits inside the bounds (a call
/// the configuration forbids is refused before a guardrail is asked about it) and outside usage
/// recording (a blocked request never reaches the provider, so it has no cost to record).
/// </para>
/// <para>
/// Redaction runs FIRST, on both paths, and its output is what the guardrails inspect and what the
/// provider is sent. A guardrail therefore never has to reason about text a redactor was going to
/// remove anyway, and nothing downstream of this layer can see the original.
/// </para>
/// </summary>
/// <remarks>
/// The streaming path inspects the request and each update as it arrives, and a block ends the
/// stream at that update. What it does NOT do is inspect the assembled answer: an update is a
/// fragment, and accumulating a whole streamed answer before releasing any of it defeats the reason
/// a caller chose streaming. That accumulation stays the caller's choice, either inside an
/// implementation of <see cref="IChatGuardrail.InspectStreamedUpdateAsync"/> or at its own call
/// site.
/// </remarks>
public sealed class GuardrailChatClient : DelegatingChatClient
{
    private readonly IChatGuardrail[] _guardrails;
    private readonly IChatRequestRedactor[] _redactors;

    /// <summary>Initializes a new instance of the <see cref="GuardrailChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="guardrails">Every guardrail to run, in registration order.</param>
    public GuardrailChatClient(IChatClient innerClient, IEnumerable<IChatGuardrail> guardrails)
        : this(innerClient, guardrails, [])
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GuardrailChatClient"/> class.</summary>
    /// <param name="innerClient">The client to wrap.</param>
    /// <param name="guardrails">Every guardrail to run, in registration order.</param>
    /// <param name="redactors">Every redactor to apply, in registration order, before any guardrail runs.</param>
    public GuardrailChatClient(
        IChatClient innerClient,
        IEnumerable<IChatGuardrail> guardrails,
        IEnumerable<IChatRequestRedactor> redactors)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(guardrails);
        ArgumentNullException.ThrowIfNull(redactors);
        _guardrails = [.. guardrails];
        _redactors = [.. redactors];
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Materialized once: the redactors, the guardrails and the inner client must see the same
        // messages, and a caller is free to hand us a lazily generated sequence.
        var inspected = Redact(Materialize(messages));

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
        var inspected = Redact(Materialize(messages));

        await InspectRequestAsync(inspected, options, cancellationToken).ConfigureAwait(false);

        await foreach (var update in base.GetStreamingResponseAsync(inspected, options, cancellationToken).ConfigureAwait(false))
        {
            // Inspected BEFORE it is yielded: once the caller has the fragment, refusing it is a
            // statement rather than a control, so the block has to happen on this side of the yield.
            foreach (var guardrail in _guardrails)
            {
                var verdict = await guardrail
                    .InspectStreamedUpdateAsync(update, options, cancellationToken)
                    .ConfigureAwait(false);

                if (!verdict.IsAllowed)
                {
                    throw new ChatGuardrailException(verdict.Reason ?? GuardrailVerdict.UnspecifiedReason);
                }
            }

            yield return update;
        }
    }

    private static IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return messages as IReadOnlyList<ChatMessage> ?? [.. messages];
    }

    private IReadOnlyList<ChatMessage> Redact(IReadOnlyList<ChatMessage> messages)
    {
        var current = messages;
        foreach (var redactor in _redactors)
        {
            current = redactor.Redact(current);
        }

        return current;
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
