using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using MMCA.Common.AI.Chat;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The one content policy the framework does ship: direct contact details are removed from every
/// outgoing message before the provider sees them.
/// <para>
/// ADR-120 says the framework ships extension points and no content policy, because what counts as
/// a prompt injection or a disallowed topic is an application decision. Contact details are the
/// exception that proves the rule: an email address and a phone number are never evidence for
/// anything a model is being asked, so sending them is pure exposure with no offsetting value, and
/// the judgement does not change between applications or jurisdictions.
/// </para>
/// <para>
/// It implements both halves deliberately. As an <see cref="IChatRequestRedactor"/> it rewrites the
/// messages; as an <see cref="IChatGuardrail"/> it allows everything, so that registering it is
/// enough to satisfy <see cref="AiSettings.RequireGuardrail"/> without also pretending to be a
/// content policy it is not.
/// </para>
/// </summary>
/// <remarks>
/// The two patterns are copied verbatim from the MMCA.ADC session scorer
/// (<c>AnthropicScoringService.Redact</c>), so this framework guardrail removes exactly what that
/// scorer removes today and the application-level copy can be deleted without changing what leaves
/// the process. Names are NOT redacted: a speaker's name is the published conference record, and it
/// is the only handle a credibility judgement has on a track record. The phone pattern is
/// deliberately narrow (a ten-digit North American shape with the usual separators and an optional
/// country code) rather than "any run of digits": prose legitimately contains years, team sizes and
/// throughput figures, and redacting those would cost the model its evidence for no privacy gain.
/// <para>
/// <see cref="ChatOptions.Instructions"/> is deliberately left alone. Instructions are the
/// application's own text, written by the application and not by a user, so an address in them is
/// there on purpose (a support mailbox the model should quote) rather than leaked.
/// </para>
/// </remarks>
public sealed partial class PiiRedactionGuardrail : IChatRequestRedactor, IChatGuardrail
{
    private const string EmailPlaceholder = "[redacted-email]";
    private const string PhonePlaceholder = "[redacted-phone]";

    /// <inheritdoc />
    public IReadOnlyList<ChatMessage> Redact(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var redacted = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            redacted.Add(RedactMessage(message));
        }

        return redacted;
    }

    /// <inheritdoc />
    public ValueTask<GuardrailVerdict> InspectRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);

    /// <inheritdoc />
    public ValueTask<GuardrailVerdict> InspectResponseAsync(
        ChatResponse response,
        ChatOptions? options,
        CancellationToken cancellationToken) => ValueTask.FromResult(GuardrailVerdict.Allow);

    [GeneratedRegex(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailPattern { get; }

    [GeneratedRegex(
        @"(?<!\d)(?:\+?\d{1,3}[ .\-]?)?\(?\d{3}\)?[ .\-]?\d{3}[ .\-]?\d{4}(?!\d)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PhonePattern { get; }

    private static ChatMessage RedactMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                contents.Add(new TextContent(RedactText(text.Text)));
            }
            else
            {
                // Images, function calls and provider-specific content pass through untouched: this
                // guardrail understands text, and silently dropping what it does not understand
                // would be a far worse failure than leaving it alone.
                contents.Add(content);
            }
        }

        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    private static string RedactText(string value) =>
        PhonePattern.Replace(EmailPattern.Replace(value, EmailPlaceholder), PhonePlaceholder);
}
