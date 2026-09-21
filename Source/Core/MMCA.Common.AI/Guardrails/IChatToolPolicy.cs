using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The extension point that decides, per request, which of the tools a caller attached the model is
/// actually offered.
/// <para>
/// <c>Ai:AllowTools</c> is a switch with two positions and no middle: off means no tool ever
/// reaches the model, on used to mean every tool a caller attached did. This interface is the middle
/// position. Registering one is mandatory while <c>Ai:AllowTools</c> is true, and while none is
/// registered <see cref="Chat.BoundedChatClient"/> strips every tool, so the failure mode is a model
/// with no tools rather than a model with all of them.
/// </para>
/// </summary>
/// <remarks>
/// EVERY registered policy must return <see cref="ToolAuthorization.Allowed"/> for a tool to be
/// offered, so policies compose by intersection and a new concern can only ever remove tools. A
/// tool marked consequential (see <see cref="ChatToolPolicy.ConsequentialPropertyKey"/>) has to
/// clear one more bar on top of that: the caller has to have named it in the request's confirmed
/// list.
/// </remarks>
public interface IChatToolPolicy
{
    /// <summary>Decides whether one tool may be offered on one request.</summary>
    /// <param name="tool">The tool the caller attached.</param>
    /// <param name="options">The options for this request, after the bounds were applied.</param>
    /// <returns><see cref="ToolAuthorization.Allowed"/> to offer it, otherwise it is stripped.</returns>
    ToolAuthorization Authorize(AITool tool, ChatOptions options);
}
