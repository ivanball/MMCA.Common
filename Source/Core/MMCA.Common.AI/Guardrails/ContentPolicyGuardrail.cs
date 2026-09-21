using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MMCA.Common.AI.Chat;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The content policy a feature cannot bypass: prompt-injection markers in USER content are removed
/// or refused, and an answer matching a configured pattern never reaches the caller.
/// <para>
/// It lives in the composition root, inside <see cref="Chat.GuardrailChatClient"/>, so it runs on
/// every call whatever the calling code believes it is doing. There is no per-call opt-out and no
/// flag a feature can set: the only way to change what it does is the <c>Ai:ContentPolicy</c>
/// section, which is a reviewable line in a settings file rather than an argument at a call site.
/// </para>
/// <para>
/// Like <see cref="PiiRedactionGuardrail"/> it implements both halves, and for the same reason: the
/// redactor rewrites what goes out, the guardrail answers yes or no, and they are two faces of one
/// decision. Registering it therefore also satisfies <see cref="AiSettings.RequireGuardrail"/>.
/// </para>
/// </summary>
/// <remarks>
/// <b>Order matters, and the pipeline already fixes it.</b>
/// <see cref="Chat.GuardrailChatClient"/> runs every <see cref="IChatRequestRedactor"/> before any
/// <see cref="IChatGuardrail"/>, so in <see cref="ContentPolicyInjectionMode.Redact"/> mode the
/// markers are already gone by the time <see cref="InspectRequestAsync"/> is asked, which is why
/// that method allows. In <see cref="ContentPolicyInjectionMode.Block"/> mode the inverse holds:
/// <see cref="Redact"/> is a pass-through precisely so the inspection sees what the caller actually
/// supplied. Reading one method without the other reads as a bug; the two modes are one behavior
/// split across the pipeline's fixed order.
/// <para>
/// <b>Only user-role content is touched.</b> System and assistant messages are the application's
/// own text and the model's own prior turns; an instruction in them is there on purpose, and
/// redacting the system prompt for saying "ignore previous instructions" would break the very
/// feature the policy is protecting.
/// </para>
/// <para>
/// <b>The built-in marker list is small and precise on purpose</b> (case-insensitive, culture
/// invariant, compiled once, with a match timeout):
/// </para>
/// <list type="bullet">
/// <item><description><c>ignore (all |any )?(previous|prior|above|earlier) instructions</c></description></item>
/// <item><description><c>disregard (the |all |your )?(system|previous|prior) (prompt|instructions)</c></description></item>
/// <item><description><c>you are now (a|an) </c></description></item>
/// <item><description><c>new instructions:</c></description></item>
/// <item><description><c>(reveal|print|show|repeat) (your|the) (system|hidden|initial) (prompt|instructions)</c></description></item>
/// <item><description><c>act as (an? )?(unrestricted|unfiltered|jailbroken)</c></description></item>
/// <item><description><c>developer mode</c></description></item>
/// <item><description><c>do anything now</c></description></item>
/// </list>
/// <para>
/// Every phrase is anchored on an imperative verb aimed at the model, never on a noun a person
/// might legitimately use. A conference proposal about "how we handle instructions in our
/// onboarding", or one promising attendees "the initial prompt we shipped", says nothing this list
/// matches, and the suite pins both sentences. A list broad enough to catch every phrasing would
/// redact ordinary prose, and a redactor that eats evidence costs more than the attack it prevents.
/// It is therefore a floor, not a detector: an application with a stricter idea of its own content
/// adds <see cref="ContentPolicySettings.AdditionalRequestPatterns"/> or registers a second
/// guardrail.
/// </para>
/// </remarks>
public sealed partial class ContentPolicyGuardrail : IChatGuardrail, IChatRequestRedactor
{
    /// <summary>The options every configured pattern is compiled with, matching the built-ins.</summary>
    internal const RegexOptions ConfiguredPatternOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>The per-match budget, so a pathological configured pattern cannot hang a request.</summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // ExplicitCapture on the built-ins only: every group in them is an alternation, never a capture
    // anything reads. A configured pattern keeps its own capture behavior, because turning it off
    // under a host would silently change what its backreferences mean.
    private const RegexOptions BuiltInPatternOptions = ConfiguredPatternOptions | RegexOptions.ExplicitCapture;

    private static readonly (string Label, Regex Pattern)[] BuiltInMarkers =
    [
        ("ignore-previous-instructions", IgnorePreviousInstructions),
        ("disregard-system-prompt", DisregardSystemPrompt),
        ("role-reassignment", RoleReassignment),
        ("new-instructions", NewInstructions),
        ("reveal-system-prompt", RevealSystemPrompt),
        ("act-as-unrestricted", ActAsUnrestricted),
        ("developer-mode", DeveloperMode),
        ("do-anything-now", DoAnythingNow),
    ];

    private readonly ContentPolicySettings _settings;
    private readonly (string Label, Regex Pattern)[] _requestMarkers;
    private readonly Regex[] _blockedResponsePatterns;

    /// <summary>Initializes a new instance of the <see cref="ContentPolicyGuardrail"/> class.</summary>
    /// <param name="settings">The bound <c>Ai:ContentPolicy</c> section.</param>
    /// <remarks>
    /// Every configured pattern is compiled here, once, at resolve time. A pattern that does not
    /// compile has already failed options validation at startup, so this constructor is not where a
    /// typo is discovered.
    /// </remarks>
    public ContentPolicyGuardrail(IOptions<ContentPolicySettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings.Value;
        _requestMarkers = [.. BuiltInMarkers, .. CompileAdditional(_settings.AdditionalRequestPatterns)];
        _blockedResponsePatterns = [.. Compile(_settings.BlockedResponsePatterns)];
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only <see cref="ChatRole.User"/> messages are rewritten, and they are rewritten into NEW
    /// instances: a caller holding its own conversation history must still hold exactly what it
    /// built. Every other message is passed through as the same instance it came in as.
    /// </remarks>
    public IReadOnlyList<ChatMessage> Redact(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (_settings.InjectionMode != ContentPolicyInjectionMode.Redact)
        {
            // Block mode inspects what the caller supplied, and Off mode does neither. Rewriting
            // here would hide the markers from the inspection that is supposed to refuse them.
            return messages;
        }

        var redacted = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);

            redacted.Add(message.Role == ChatRole.User ? RedactMessage(message) : message);
        }

        return redacted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Allows in every mode except <see cref="ContentPolicyInjectionMode.Block"/>, because in
    /// <see cref="ContentPolicyInjectionMode.Redact"/> mode the redactor has already run and there
    /// is nothing left to refuse.
    /// </remarks>
    public ValueTask<GuardrailVerdict> InspectRequestAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (_settings.InjectionMode != ContentPolicyInjectionMode.Block)
        {
            return ValueTask.FromResult(GuardrailVerdict.Allow);
        }

        var label = FindFirstMarker(messages);

        return ValueTask.FromResult(label is null
            ? GuardrailVerdict.Allow
            : GuardrailVerdict.Block(
                $"Blocked by the content policy: user content matched the prompt-injection marker '{label}'."));
    }

    /// <inheritdoc />
    public ValueTask<GuardrailVerdict> InspectResponseAsync(
        ChatResponse response,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        return ValueTask.FromResult(InspectAnswerText(response.Text));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The same check as <see cref="InspectResponseAsync"/>, on the fragment rather than the answer.
    /// A pattern that spans two updates is therefore missed on the streaming path: accumulating the
    /// whole answer before releasing any of it defeats the reason a caller chose streaming, so a
    /// host that needs whole-answer certainty uses the buffered path.
    /// </remarks>
    public ValueTask<GuardrailVerdict> InspectStreamedUpdateAsync(
        ChatResponseUpdate update,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        return ValueTask.FromResult(InspectAnswerText(update.Text));
    }

    [GeneratedRegex(
        @"ignore (all |any )?(previous|prior|above|earlier) instructions",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex IgnorePreviousInstructions { get; }

    [GeneratedRegex(
        @"disregard (the |all |your )?(system|previous|prior) (prompt|instructions)",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DisregardSystemPrompt { get; }

    [GeneratedRegex(
        @"you are now (a|an) ",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RoleReassignment { get; }

    [GeneratedRegex(
        @"new instructions:",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex NewInstructions { get; }

    [GeneratedRegex(
        @"(reveal|print|show|repeat) (your|the) (system|hidden|initial) (prompt|instructions)",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RevealSystemPrompt { get; }

    [GeneratedRegex(
        @"act as (an? )?(unrestricted|unfiltered|jailbroken)",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ActAsUnrestricted { get; }

    [GeneratedRegex(
        @"developer mode",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeveloperMode { get; }

    [GeneratedRegex(
        @"do anything now",
        BuiltInPatternOptions,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DoAnythingNow { get; }

    private static IEnumerable<(string Label, Regex Pattern)> CompileAdditional(IReadOnlyList<string>? patterns)
    {
        if (patterns is null)
        {
            yield break;
        }

        for (var index = 0; index < patterns.Count; index++)
        {
            yield return (
                $"{nameof(ContentPolicySettings.AdditionalRequestPatterns)}[{index.ToString(CultureInfo.InvariantCulture)}]",
                new Regex(patterns[index], ConfiguredPatternOptions, MatchTimeout));
        }
    }

    private static IEnumerable<Regex> Compile(IReadOnlyList<string>? patterns)
    {
        if (patterns is null)
        {
            yield break;
        }

        foreach (var pattern in patterns)
        {
            yield return new Regex(pattern, ConfiguredPatternOptions, MatchTimeout);
        }
    }

    private GuardrailVerdict InspectAnswerText(string? text)
    {
        if (_blockedResponsePatterns.Length == 0 || string.IsNullOrEmpty(text))
        {
            return GuardrailVerdict.Allow;
        }

        for (var index = 0; index < _blockedResponsePatterns.Length; index++)
        {
            if (_blockedResponsePatterns[index].IsMatch(text))
            {
                // The index, never the text: the reason reaches the caller, and the whole point of
                // the rule was that this text should not.
                return GuardrailVerdict.Block(
                    "Blocked by the content policy: the answer matched "
                    + $"{ContentPolicySettings.SectionName}:"
                    + $"{nameof(ContentPolicySettings.BlockedResponsePatterns)}[{index.ToString(CultureInfo.InvariantCulture)}].");
            }
        }

        return GuardrailVerdict.Allow;
    }

    private string? FindFirstMarker(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message is null || message.Role != ChatRole.User)
            {
                continue;
            }

            var label = FindFirstMarker(message);
            if (label is not null)
            {
                return label;
            }
        }

        return null;
    }

    private string? FindFirstMarker(ChatMessage message)
    {
        foreach (var content in message.Contents)
        {
            if (content is not TextContent text || string.IsNullOrEmpty(text.Text))
            {
                continue;
            }

            var match = Array.Find(_requestMarkers, marker => marker.Pattern.IsMatch(text.Text));
            if (match.Label is not null)
            {
                return match.Label;
            }
        }

        return null;
    }

    private ChatMessage RedactMessage(ChatMessage message)
    {
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

    private string RedactText(string value)
    {
        var current = value;
        foreach (var marker in _requestMarkers)
        {
            current = marker.Pattern.Replace(current, _settings.RedactionPlaceholder);
        }

        return current;
    }
}
