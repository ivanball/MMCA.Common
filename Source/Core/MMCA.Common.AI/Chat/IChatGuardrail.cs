using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Chat;

/// <summary>
/// The extension point an application implements to inspect what goes to the model and what comes
/// back, and to refuse either.
/// <para>
/// The framework ships the extension point and no policy (ADR-120). What counts as a prompt
/// injection, a leaked secret, an off-topic answer or a disallowed topic is an application decision
/// that depends on the data the application holds and the jurisdiction it operates in, so a content
/// rule baked into a shared package would be wrong somewhere by construction. Register one
/// implementation per concern; every registered guardrail runs, and the first block stops the call.
/// </para>
/// </summary>
/// <remarks>
/// A guardrail runs on the hot path of every chat call, so an implementation that needs a remote
/// classifier should carry its own timeout: it inherits the caller's token, not a budget of its own.
/// </remarks>
public interface IChatGuardrail
{
    /// <summary>Inspects an outgoing request before it reaches the model.</summary>
    /// <param name="messages">The messages about to be sent.</param>
    /// <param name="options">The options the call carries, including the prompt contract, if any.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A verdict; a block stops the call before the provider is reached.</returns>
    ValueTask<GuardrailVerdict> InspectRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Inspects a completed response before it reaches the caller.</summary>
    /// <param name="response">The response the model returned.</param>
    /// <param name="options">The options the call carried.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>A verdict; a block stops the response from being returned.</returns>
    ValueTask<GuardrailVerdict> InspectResponseAsync(
        ChatResponse response,
        ChatOptions? options,
        CancellationToken cancellationToken);
}
