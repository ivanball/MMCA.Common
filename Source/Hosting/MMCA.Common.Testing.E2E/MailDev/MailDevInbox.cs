using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MMCA.Common.MailDev;

namespace MMCA.Common.Testing.E2E.MailDev;

/// <summary>
/// Reads the emails a stack sent from MailDev, the AppHost's local SMTP sink (<c>AddMailDev</c> in
/// <c>MMCA.Common.Aspire.Hosting</c>), through its REST API. The default address is the IPv4 loopback
/// on the port <c>AddMailDev</c> publishes (both read the linked <c>MailDevDefaults</c>, so they cannot
/// drift), overridable with <c>E2E_MAILDEV_URL</c>. It is the IPv4 loopback, not "localhost": on the
/// CI runners Docker publishes the port on IPv4 only, while Playwright's client tries ::1 first.
/// <para>
/// Email is delivered through the outbox after the request that caused it answers, so a read can be
/// early: callers wait with <see cref="WaitForMessageAsync"/> or
/// <see cref="WaitForMessageWithSubjectPrefixAsync"/>. A stack without a reachable MailDev cannot run
/// the email cases, so a test checks <see cref="UnreachableReasonAsync"/> first and fails or skips with
/// that reason rather than timing out on an empty inbox.
/// </para>
/// </summary>
public static class MailDevInbox
{
    /// <summary>The environment variable that overrides <see cref="BaseUrl"/>.</summary>
    public const string BaseUrlVariable = "E2E_MAILDEV_URL";

    /// <summary>Gets the MailDev base URL, without a trailing slash.</summary>
    public static string BaseUrl =>
        (Environment.GetEnvironmentVariable(BaseUrlVariable) is { Length: > 0 } configured
            ? configured
            : string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{MailDevDefaults.HttpPort}")).TrimEnd('/');

    /// <summary>
    /// Returns why the inbox cannot be read (the HTTP status or the transport error), or null when it
    /// can, so a failure or a skip names the cause instead of only the fact.
    /// </summary>
    /// <param name="api">Any request context, such as <c>Page.APIRequest</c>.</param>
    /// <returns>The reason, or null when MailDev answers.</returns>
    public static async Task<string?> UnreachableReasonAsync(IAPIRequestContext api)
    {
        ArgumentNullException.ThrowIfNull(api);

        try
        {
            var response = await GetMessageListAsync(api, 5_000).ConfigureAwait(false);
            return response.Ok
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"HTTP {response.Status}");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }
    }

    /// <summary>All messages to <paramref name="toAddress"/> whose subject is exactly <paramref name="subject"/>, oldest first.</summary>
    /// <param name="api">Any request context.</param>
    /// <param name="toAddress">The recipient to match (case-insensitive).</param>
    /// <param name="subject">The exact subject.</param>
    /// <returns>The matching messages.</returns>
    public static async Task<IReadOnlyList<MailDevMessage>> MessagesAsync(IAPIRequestContext api, string toAddress, string subject)
    {
        var all = await ReadAllAsync(api).ConfigureAwait(false);
        return [.. all.Where(m => IsTo(m, toAddress) && string.Equals(m.Subject, subject, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Polls MailDev until the number of messages to <paramref name="toAddress"/> whose subject is
    /// exactly <paramref name="subject"/> reaches <paramref name="count"/>, and returns the newest one.
    /// </summary>
    /// <param name="api">Any request context.</param>
    /// <param name="toAddress">The recipient to match (case-insensitive).</param>
    /// <param name="subject">The exact subject.</param>
    /// <param name="count">How many such messages must exist.</param>
    /// <param name="timeoutSeconds">How long to wait.</param>
    /// <returns>The newest matching message.</returns>
    public static Task<MailDevMessage> WaitForMessageAsync(
        IAPIRequestContext api,
        string toAddress,
        string subject,
        int count = 1,
        int timeoutSeconds = 60) =>
        WaitForAsync(
            api,
            toAddress,
            m => string.Equals(m.Subject, subject, StringComparison.Ordinal),
            count,
            timeoutSeconds,
            $"with the subject '{subject}'");

    /// <summary>
    /// Polls MailDev until a message to <paramref name="toAddress"/> whose subject starts with
    /// <paramref name="subjectPrefix"/> arrives, and returns the newest such message. For subjects that
    /// carry a value the test cannot know in advance, such as a generated order number.
    /// </summary>
    /// <param name="api">Any request context.</param>
    /// <param name="toAddress">The recipient to match (case-insensitive).</param>
    /// <param name="subjectPrefix">The subject prefix to match.</param>
    /// <param name="timeoutSeconds">How long to wait.</param>
    /// <returns>The newest matching message.</returns>
    public static Task<MailDevMessage> WaitForMessageWithSubjectPrefixAsync(
        IAPIRequestContext api,
        string toAddress,
        string subjectPrefix,
        int timeoutSeconds = 90) =>
        WaitForAsync(
            api,
            toAddress,
            m => m.Subject.StartsWith(subjectPrefix, StringComparison.Ordinal),
            count: 1,
            timeoutSeconds,
            $"with a subject starting '{subjectPrefix}'");

    /// <summary>The first link in <paramref name="message"/> whose target contains <paramref name="pathFragment"/>.</summary>
    /// <param name="message">The message.</param>
    /// <param name="pathFragment">A substring of the wanted href, such as a page path.</param>
    /// <returns>The absolute link.</returns>
    public static Uri LinkContaining(MailDevMessage message, string pathFragment)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(pathFragment);

        var match = Regex.Matches(message.Html, "href=\"(?<href>[^\"]+)\"", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1))
            .Select(m => m.Groups["href"].Value)
            .FirstOrDefault(href => href.Contains(pathFragment, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The email '{message.Subject}' carries no link containing '{pathFragment}'.");
        return new Uri(match);
    }

    /// <summary>Parses MailDev's message-list JSON. Public so the parsing can be pinned without a running MailDev.</summary>
    /// <param name="json">The body of <c>GET /api/email</c>.</param>
    /// <returns>The messages, in MailDev's order (oldest first).</returns>
    public static IReadOnlyList<MailDevMessage> ParseMessages(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var messages = new List<MailDevMessage>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
            var subject = element.TryGetProperty("subject", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            var html = element.TryGetProperty("html", out var h) && h.ValueKind == JsonValueKind.String
                ? WebUtility.HtmlDecode(h.GetString() ?? string.Empty)
                : string.Empty;
            messages.Add(new MailDevMessage(id, subject, html, Recipients(element)));
        }

        return messages;
    }

    private static List<string> Recipients(JsonElement message) =>
        message.TryGetProperty("to", out var toArray) && toArray.ValueKind == JsonValueKind.Array
            ? [.. toArray.EnumerateArray()
                .Select(static r => r.TryGetProperty("address", out var address) ? address.GetString() : null)
                .OfType<string>()]
            : [];

    private static bool IsTo(MailDevMessage message, string toAddress) =>
        message.To.Any(to => string.Equals(to, toAddress, StringComparison.OrdinalIgnoreCase));

    private static async Task<MailDevMessage> WaitForAsync(
        IAPIRequestContext api,
        string toAddress,
        Func<MailDevMessage, bool> subjectMatches,
        int count,
        int timeoutSeconds,
        string subjectDescription)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var matches = (await ReadAllAsync(api).ConfigureAwait(false))
                .Where(m => IsTo(m, toAddress) && subjectMatches(m))
                .ToList();
            if (matches.Count >= count)
            {
                return matches[^1];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Only {matches.Count} of {count} emails to {toAddress} {subjectDescription} arrived in MailDev at {BaseUrl} within {timeoutSeconds}s."));
            }

            await Task.Delay(1_000).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads MailDev's message list. MailDev 3 serves it at <c>/api/email</c> and answers 404 on the
    /// version-2 path <c>/email</c>; the AppHost pins 3.x (<c>MailDevImageTag</c>), but a developer's
    /// persistent container created before the pin may still be 2.x, so the newer path is tried first.
    /// </summary>
    private static async Task<IAPIResponse> GetMessageListAsync(IAPIRequestContext api, float timeoutMs)
    {
        var response = await api.GetAsync(BaseUrl + "/api/email", new() { Timeout = timeoutMs }).ConfigureAwait(false);
        return response.Status == 404
            ? await api.GetAsync(BaseUrl + "/email", new() { Timeout = timeoutMs }).ConfigureAwait(false)
            : response;
    }

    private static async Task<IReadOnlyList<MailDevMessage>> ReadAllAsync(IAPIRequestContext api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var response = await GetMessageListAsync(api, 10_000).ConfigureAwait(false);
        return ParseMessages(await response.TextAsync().ConfigureAwait(false));
    }
}
