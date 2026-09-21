namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// What <see cref="ContentPolicyGuardrail"/> does when user-supplied content carries a
/// prompt-injection marker.
/// </summary>
/// <remarks>
/// Three answers rather than two, because the right one depends on what the content is. A speaker
/// bio or a support ticket is evidence the model still needs, so removing the instruction and
/// keeping the rest (<see cref="Redact"/>, the default) loses nothing worth keeping. A document an
/// untrusted party uploaded is a different judgement: there, a marker is a reason to refuse the
/// whole call (<see cref="Block"/>). <see cref="Off"/> exists so a host that has its own detector
/// can keep the registration (and with it <see cref="AiSettings.RequireGuardrail"/>) without
/// running this one twice.
/// </remarks>
public enum ContentPolicyInjectionMode
{
    /// <summary>
    /// Replace every marker match in user-role content with
    /// <see cref="ContentPolicySettings.RedactionPlaceholder"/> and let the call proceed. The
    /// default.
    /// </summary>
    Redact = 0,

    /// <summary>
    /// Leave the content alone and refuse the call, naming the marker that matched. Redaction is a
    /// pass-through in this mode, so the guardrail inspects exactly what the caller supplied.
    /// </summary>
    Block = 1,

    /// <summary>
    /// Do neither: user content is neither rewritten nor refused on injection markers. Configured
    /// response patterns still apply, because they are a separate axis.
    /// </summary>
    Off = 2,
}
