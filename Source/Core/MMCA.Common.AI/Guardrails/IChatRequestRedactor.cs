using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The extension point an application implements to REWRITE what goes to the model, as opposed to
/// <see cref="Chat.IChatGuardrail"/>, which only answers yes or no.
/// <para>
/// A guardrail refuses a call; a redactor lets it through with less in it. That is the difference
/// between "this bio mentions a phone number, so no scoring for this speaker" and "score the bio
/// without the phone number", and for contact details the second answer is almost always the one an
/// application wants.
/// </para>
/// <para>
/// Every registered redactor runs, in registration order, on the materialized messages BEFORE any
/// guardrail inspects them, on both the buffered and the streaming path, and the redacted list is
/// what the provider is sent. So a guardrail never has to reason about text the redactor was going
/// to remove anyway, and nothing downstream of this layer can see the original.
/// </para>
/// </summary>
/// <remarks>
/// A redactor runs on the hot path of every chat call. Implementations should be allocation-light
/// and must not reach the network.
/// </remarks>
public interface IChatRequestRedactor
{
    /// <summary>Returns the messages to send in place of the ones the caller supplied.</summary>
    /// <param name="messages">The messages the caller passed, which must not be modified.</param>
    /// <returns>New <see cref="ChatMessage"/> instances carrying the redacted content.</returns>
    /// <remarks>
    /// The contract is NEW instances, never a mutation: a caller that keeps its own message list (a
    /// conversation history, a retry buffer, an audit record) must still hold exactly what it built.
    /// An implementation that edited the incoming messages in place would silently rewrite the
    /// caller's own data as a side effect of sending it.
    /// </remarks>
    IReadOnlyList<ChatMessage> Redact(IReadOnlyList<ChatMessage> messages);
}
