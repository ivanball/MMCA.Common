namespace MMCA.Common.AI.Chat;

/// <summary>
/// Thrown by <see cref="GuardrailChatClient"/> when a registered <see cref="IChatGuardrail"/> refuses
/// a request or a response.
/// </summary>
/// <remarks>
/// A refusal is an exception rather than an empty response so it cannot be mistaken for the model
/// having nothing to say, and so it surfaces at the call site with the reason attached. Callers that
/// want a graceful fallback catch this type specifically.
/// </remarks>
public sealed class ChatGuardrailException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ChatGuardrailException"/> class.</summary>
    public ChatGuardrailException()
        : base(GuardrailVerdict.UnspecifiedReason)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChatGuardrailException"/> class.</summary>
    /// <param name="message">The reason the guardrail reported.</param>
    public ChatGuardrailException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ChatGuardrailException"/> class.</summary>
    /// <param name="message">The reason the guardrail reported.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public ChatGuardrailException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
