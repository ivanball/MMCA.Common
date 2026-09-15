namespace MMCA.Common.AI.Chat;

/// <summary>
/// The answer one <see cref="IChatGuardrail"/> gives about one request or one response: let it
/// through, or stop it and say why.
/// </summary>
/// <remarks>
/// A struct rather than a class because a guardrail answers on every call and the common answer
/// carries no payload. <see langword="default"/> reads as a block with
/// <see cref="UnspecifiedReason"/>, so a half-built verdict fails closed rather than silently
/// admitting the request.
/// </remarks>
public readonly record struct GuardrailVerdict
{
    /// <summary>The reason reported for a block that supplied none of its own.</summary>
    public const string UnspecifiedReason = "Blocked by a chat guardrail.";

    private GuardrailVerdict(bool isAllowed, string? reason)
    {
        IsAllowed = isAllowed;
        Reason = reason;
    }

    /// <summary>The verdict that lets the call proceed.</summary>
    public static GuardrailVerdict Allow => new(isAllowed: true, reason: null);

    /// <summary>Whether the inspected request or response may proceed.</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// Why the call was blocked, or <see langword="null"/> when it was allowed (and on the
    /// <see langword="default"/> value, which is why <see cref="UnspecifiedReason"/> exists).
    /// </summary>
    public string? Reason { get; }

    /// <summary>Builds a blocking verdict carrying the reason reported to the caller.</summary>
    /// <param name="reason">Why the call is refused. Reaches the caller, so it must be safe to surface.</param>
    /// <returns>A verdict that stops the call.</returns>
    public static GuardrailVerdict Block(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new GuardrailVerdict(isAllowed: false, reason);
    }
}
