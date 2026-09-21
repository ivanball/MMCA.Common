using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The configuration surface of <see cref="ContentPolicyGuardrail"/>, bound from the
/// <c>Ai:ContentPolicy</c> section by <c>AddContentPolicyGuardrail(configuration)</c>.
/// <para>
/// Deliberately a section of its own rather than more properties on <see cref="AiSettings"/>: a
/// host that never calls the registration method has nothing to configure and nothing to read, so
/// adopting the rest of the AI package stays exactly as cheap as it was.
/// </para>
/// </summary>
/// <remarks>
/// The built-in injection markers are code, not configuration: they are the small, precise list the
/// framework is willing to defend, and a host that disagrees with one of them turns the whole check
/// off rather than editing it in a settings file. What IS configurable is the part that depends on
/// the application: extra request patterns for the vocabulary of its own domain, and response
/// patterns for the strings its answers must never contain.
/// </remarks>
public sealed class ContentPolicySettings : IValidatableObject
{
    /// <summary>Configuration section name, relative to the root.</summary>
    public const string SectionName = "Ai:ContentPolicy";

    /// <summary>The placeholder written in place of a marker when none is configured.</summary>
    public const string DefaultRedactionPlaceholder = "[redacted-instruction]";

    /// <summary>
    /// What to do when user-role content carries a prompt-injection marker.
    /// <see cref="ContentPolicyInjectionMode.Redact"/> by default.
    /// </summary>
    public ContentPolicyInjectionMode InjectionMode { get; init; } = ContentPolicyInjectionMode.Redact;

    /// <summary>
    /// Extra .NET regular expressions, as source text, merged with the built-in marker list and
    /// applied to user-role content exactly as the built-ins are. Empty by default.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively and with a match timeout, like the built-ins. An entry that does
    /// not compile fails validation at startup rather than on a user's request.
    /// </remarks>
    public IReadOnlyList<string> AdditionalRequestPatterns { get; init; } = [];

    /// <summary>
    /// .NET regular expressions, as source text, that an answer must not match. Empty by default,
    /// which switches the response half of the policy off.
    /// </summary>
    /// <remarks>
    /// This is the half that cannot be expressed as redaction: a model that has already said the
    /// thing cannot unsay it, so the only available answer is to refuse the response. The block
    /// reason names the pattern by index and never echoes the text that matched, because the reason
    /// reaches the caller and the point of the rule was that the text should not.
    /// </remarks>
    public IReadOnlyList<string> BlockedResponsePatterns { get; init; } = [];

    /// <summary>
    /// What replaces a marker match in <see cref="ContentPolicyInjectionMode.Redact"/> mode.
    /// Defaults to <see cref="DefaultRedactionPlaceholder"/>.
    /// </summary>
    /// <remarks>
    /// A visible placeholder rather than an empty string on purpose: the model should be able to
    /// see that something was removed, and a reviewer reading a trace should be able to tell a
    /// redaction from a sentence the user never wrote.
    /// </remarks>
    [Required]
    public string RedactionPlaceholder { get; init; } = DefaultRedactionPlaceholder;

    /// <summary>
    /// Fails the host at startup when a configured pattern is not a valid .NET regular expression,
    /// naming the offending pattern.
    /// </summary>
    /// <param name="validationContext">The validation context (unused: every rule here is local).</param>
    /// <returns>One result per unusable pattern; empty when the settings are usable.</returns>
    /// <remarks>
    /// A bad pattern is a deployment failure rather than a first-request one for the same reason the
    /// guardrail requirement is: a policy that throws the first time a user trips it is not a
    /// policy, and the person who can fix a typo in a settings file is the one deploying it.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidatePatterns(AdditionalRequestPatterns, nameof(AdditionalRequestPatterns)))
        {
            yield return result;
        }

        foreach (var result in ValidatePatterns(BlockedResponsePatterns, nameof(BlockedResponsePatterns)))
        {
            yield return result;
        }
    }

    private static IEnumerable<ValidationResult> ValidatePatterns(IReadOnlyList<string> patterns, string memberName)
    {
        if (patterns is null)
        {
            yield break;
        }

        for (var index = 0; index < patterns.Count; index++)
        {
            var failure = DescribeFailure(patterns[index]);
            if (failure is not null)
            {
                yield return new ValidationResult(
                    $"{SectionName}:{memberName}[{index.ToString(CultureInfo.InvariantCulture)}] is not a valid "
                    + ".NET regular expression. "
                    + $"Pattern: '{patterns[index]}'. {failure}",
                    [memberName]);
            }
        }
    }

    private static string? DescribeFailure(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "An empty pattern matches everything, which is never what a policy meant.";
        }

        try
        {
            _ = new Regex(pattern, ContentPolicyGuardrail.ConfiguredPatternOptions, ContentPolicyGuardrail.MatchTimeout);
            return null;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }
}
