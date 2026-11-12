using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.Shared.Webhooks;

/// <summary>Replaces an outbound webhook subscription's target URL, description and event types.</summary>
/// <param name="Url">The absolute https target URL (http and local targets only in Development, when the host allows them).</param>
/// <param name="Description">An optional label of up to 200 characters.</param>
/// <param name="EventTypes">1 to 20 registered webhook event types, or <c>"*"</c> for every one.</param>
[SuppressMessage(
    "Design",
    "CA1054:URI-like parameters should not be strings",
    Justification = "The target URL is untrusted operator input carried verbatim over the wire and validated as text (scheme, user info, fragment, length) before anything parses it; System.Uri would re-normalize it.")]
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "The target URL is untrusted operator input carried verbatim over the wire and validated as text (scheme, user info, fragment, length) before anything parses it; System.Uri would re-normalize it.")]
public sealed record UpdateWebhookSubscriptionRequest(string Url, string? Description, IReadOnlyList<string> EventTypes);
