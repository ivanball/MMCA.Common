namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// What one <see cref="IChatToolPolicy"/> says about offering one tool on one request.
/// </summary>
/// <remarks>
/// <see cref="Denied"/> is the zero value on purpose: a policy that forgets to answer, or a value
/// that was never assigned, refuses the tool rather than offering it.
/// </remarks>
public enum ToolAuthorization
{
    /// <summary>The tool is not offered to the model on this request.</summary>
    Denied,

    /// <summary>The tool may be offered, unless another policy or the confirmation rule refuses it.</summary>
    Allowed,
}
