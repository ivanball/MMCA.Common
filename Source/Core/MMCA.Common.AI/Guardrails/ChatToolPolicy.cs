using System.Globalization;
using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The two well-known property keys the tool-policy layer reads: one that marks a tool as
/// consequential, and one that carries the tools a caller has confirmed for the request in hand.
/// <para>
/// They are plain string keys on the property bags Microsoft.Extensions.AI already carries
/// (<see cref="AITool.AdditionalProperties"/> and <see cref="ChatOptions.AdditionalProperties"/>)
/// rather than types of our own, so a tool built by any factory and options built by any caller can
/// participate without taking a dependency on this package.
/// </para>
/// </summary>
/// <remarks>
/// The distinction the keys encode is the one that matters for an agent: reading is recoverable and
/// writing is not. A tool that looks something up can be offered on a policy decision made once, at
/// registration; a tool that sends an email, moves money or deletes a row is a decision a human
/// makes per request, and a model arguing convincingly for it is exactly the case the confirmation
/// exists to survive.
/// </remarks>
public static class ChatToolPolicy
{
    /// <summary>
    /// The <see cref="AITool.AdditionalProperties"/> key whose value <see langword="true"/> marks a
    /// tool as consequential: it does something that cannot be undone by not reading the answer.
    /// </summary>
    public const string ConsequentialPropertyKey = "mmca.tool.consequential";

    /// <summary>
    /// The <see cref="ChatOptions.AdditionalProperties"/> key holding the names of the tools the
    /// caller has confirmed for THIS request, as an <see cref="IEnumerable{T}"/> of
    /// <see cref="string"/> or as a single <see cref="string"/>.
    /// </summary>
    /// <remarks>
    /// Per request, never per session: a confirmation that outlived the request it was given for
    /// would be a standing grant, which is the thing a confirmation step exists to avoid.
    /// </remarks>
    public const string ConfirmedToolsPropertyKey = "mmca.tool.confirmed";

    /// <summary>Answers whether a tool declares itself consequential.</summary>
    /// <param name="tool">The tool to read.</param>
    /// <returns><see langword="true"/> when the tool carries the marker with a true value.</returns>
    internal static bool IsConsequential(AITool tool)
    {
        if (!tool.AdditionalProperties.TryGetValue(ConsequentialPropertyKey, out var value))
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,

            // Configuration and JSON both hand a boolean over as text, and a tool declared
            // consequential in a settings file must not read as harmless because of it.
            string text => bool.TryParse(text, out var parsed) && parsed,
            _ => false,
        };
    }

    /// <summary>Reads the tool names the caller confirmed for this request.</summary>
    /// <param name="options">The options for this request.</param>
    /// <returns>The confirmed names, empty when the caller confirmed nothing.</returns>
    internal static IReadOnlyCollection<string> ReadConfirmedTools(ChatOptions options)
    {
        if (options.AdditionalProperties is not { } properties
            || !properties.TryGetValue(ConfirmedToolsPropertyKey, out var value))
        {
            return [];
        }

        return value switch
        {
            string single => [single],
            IEnumerable<string> many => [.. many],

            // A non-generic sequence (a JSON array bound as object[], say) still names tools.
            System.Collections.IEnumerable sequence =>
                [.. sequence.Cast<object?>()
                    .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)],
            _ => [],
        };
    }
}
