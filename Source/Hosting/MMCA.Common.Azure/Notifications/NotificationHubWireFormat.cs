using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// The bodies the hub exchanges: the installation JSON written on upsert and read back on get, the
/// Atom feed of registrations, and the translation of a failed response into a
/// <see cref="NotificationHubException"/>. Every shape matches what the retired
/// Microsoft.Azure.NotificationHubs 4.2.0 SDK wrote and accepted (installation JSON property order,
/// null handling and string escaping included), so the hub sees byte-identical requests.
/// </summary>
internal static partial class NotificationHubWireFormat
{
    /// <summary>The namespace the registration descriptions inside the Atom entries live in.</summary>
    private const string RegistrationNamespace = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    /// <summary>The longest tag the hub accepts on a registration.</summary>
    private const int MaximumTagSize = 120;

    private const RegexOptions TagPatternOptions = RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture;

    // The three non-ASCII characters the SDK's JSON writer escaped.
    private const char NextLine = (char)0x85;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>A 429 that names no wait is retried after ten seconds.</summary>
    private static readonly TimeSpan DefaultThrottleWait = TimeSpan.FromSeconds(10);

    /// <summary>The escape for each ASCII character that needs one, null for the rest.</summary>
    private static readonly string?[] AsciiEscapes = BuildAsciiEscapes();

    /// <summary>
    /// The installation JSON: <c>installationId</c>, <c>userId</c>, <c>pushChannel</c>,
    /// <c>pushChannelExpired</c>, <c>platform</c> and <c>expirationTime</c> always present (null when
    /// unset), then <c>tags</c> only when set, unindented.
    /// </summary>
    internal static string WriteInstallation(NotificationHubInstallation installation)
    {
        var json = new StringBuilder(256);
        json.Append("{\"installationId\":");
        AppendString(json, installation.InstallationId);
        json.Append(",\"userId\":null,\"pushChannel\":");
        AppendString(json, installation.PushChannel);
        json.Append(",\"pushChannelExpired\":null,\"platform\":");
        AppendString(json, installation.Platform switch
        {
            NotificationHubPlatform.FcmV1 => "fcmV1",
            NotificationHubPlatform.Apns => "apns",
            _ => throw new ArgumentOutOfRangeException(nameof(installation), installation.Platform, "Unsupported platform."),
        });
        json.Append(",\"expirationTime\":null");
        if (installation.Tags is { } tags)
        {
            json.Append(",\"tags\":[");
            for (var i = 0; i < tags.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                AppendString(json, tags[i]);
            }

            json.Append(']');
        }

        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// Reads the members the registrar needs out of an installation the hub returned. Property names
    /// match case-insensitively; an empty or <see langword="null"/> body reads as no installation.
    /// </summary>
    /// <exception cref="JsonException">The body is not a JSON object of the installation shape.</exception>
    internal static NotificationHubInstallationState? ReadInstallation(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The notification hub returned an installation that is not a JSON object.");
        }

        string? pushChannel = null;
        List<string>? tags = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "pushChannel", StringComparison.OrdinalIgnoreCase))
            {
                pushChannel = ReadNullableString(property.Value);
            }
            else if (string.Equals(property.Name, "tags", StringComparison.OrdinalIgnoreCase))
            {
                tags = ReadTags(property.Value);
            }
        }

        return new NotificationHubInstallationState(pushChannel, tags);
    }

    private static List<string>? ReadTags(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.Array
            ? [.. element.EnumerateArray().Select(ReadNullableString).OfType<string>()]
            : throw new JsonException("The notification hub returned installation tags that are not an array.");
    }

    /// <summary>
    /// Reads the tags of every registration in an Atom feed (<c>feed/entry/content/*RegistrationDescription/Tags</c>).
    /// A registration's comma-separated tag string becomes a case-insensitive set; one that fails the
    /// hub's tag rules (a malformed tag, more than one installation or user system tag, a tag over 120
    /// characters) reads as no tags.
    /// </summary>
    /// <exception cref="FormatException">The body is not an Atom feed.</exception>
    internal static async Task<List<IReadOnlySet<string>>> ReadRegistrationTagsAsync(Stream source)
    {
        var result = new List<IReadOnlySet<string>>();
        using var reader = XmlReader.Create(source, new XmlReaderSettings { Async = true });

        await reader.MoveToContentAsync().ConfigureAwait(false);
        if (!reader.IsStartElement("feed"))
        {
            throw new FormatException("Required 'feed' element is missing");
        }

        while (reader.ReadToFollowing("entry"))
        {
            if (!reader.ReadToDescendant("content"))
            {
                continue;
            }

            reader.ReadStartElement();
            await reader.MoveToContentAsync().ConfigureAwait(false);
            result.Add(await ReadDescriptionTagsAsync(reader).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>
    /// Maps a non-success response to the exception the client throws: 404 (and an unexpected 204) is
    /// not-found; 500, 503, 504 and 408 are transient; 429, and a 403 carrying <c>Retry-After</c>, are
    /// throttling, transient with the hub's wait (ten seconds when a 429 names none); everything else
    /// (400, 401, 403, 409, 410, ...) is a permanent failure.
    /// </summary>
    internal static async Task<NotificationHubException> TranslateFailureAsync(HttpResponseMessage response, string trackingId)
    {
        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var statusCode = response.StatusCode;
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"The notification hub answered {(int)statusCode} {statusCode}: {ReadErrorDetail(responseBody) ?? response.ReasonPhrase}. TrackingId:{trackingId}");
        var retryAfter = response.Headers.RetryAfter?.Delta;

        if (statusCode == HttpStatusCode.TooManyRequests || statusCode == HttpStatusCode.Forbidden && retryAfter is not null)
        {
            return new NotificationHubException(message, statusCode, isTransient: true, retryAfter ?? DefaultThrottleWait);
        }

        return new NotificationHubException(message, statusCode, isTransient: IsServerBusy(statusCode));
    }

    /// <summary>The statuses the hub answers while busy, worth a retry.</summary>
    private static bool IsServerBusy(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout;

    /// <summary>
    /// Maps a transport failure: a socket error that names an unreachable host is permanent, any other
    /// socket error (a timed-out or reset connection) is transient, and anything else is permanent.
    /// </summary>
    internal static NotificationHubException TranslateTransportFailure(HttpRequestException exception, string trackingId)
    {
        var isTransient = exception.GetBaseException() is SocketException socketException
            && socketException.SocketErrorCode is not (SocketError.AddressNotAvailable
                or SocketError.ConnectionRefused
                or SocketError.AccessDenied
                or SocketError.HostUnreachable
                or SocketError.HostNotFound);
        return new NotificationHubException(
            $"The notification hub could not be reached. TrackingId:{trackingId}",
            statusCode: null,
            isTransient,
            innerException: exception);
    }

    /// <summary>
    /// Writes a JSON string the way the SDK's serializer did: <c>"</c>, <c>\</c> and the control
    /// characters escaped (the common ones by letter, the rest as lower-case <c>\u00xx</c>), plus
    /// U+0085, U+2028 and U+2029; every other character, non-ASCII included, written as is.
    /// </summary>
    private static void AppendString(StringBuilder json, string? value)
    {
        if (value is null)
        {
            json.Append("null");
            return;
        }

        json.Append('"');
        foreach (var c in value)
        {
            if (c < AsciiEscapes.Length && AsciiEscapes[c] is { } escape)
            {
                json.Append(escape);
            }
            else if (c is NextLine or LineSeparator or ParagraphSeparator)
            {
                json.Append(UnicodeEscape(c));
            }
            else
            {
                json.Append(c);
            }
        }

        json.Append('"');
    }

    /// <summary>The escape for each ASCII character that needs one (null for the rest).</summary>
    private static string?[] BuildAsciiEscapes()
    {
        var escapes = new string?[128];
        for (var c = 0; c < ' '; c++)
        {
            escapes[c] = UnicodeEscape((char)c);
        }

        escapes['\t'] = "\\t";
        escapes['\n'] = "\\n";
        escapes['\r'] = "\\r";
        escapes['\f'] = "\\f";
        escapes['\b'] = "\\b";
        escapes['"'] = "\\\"";
        escapes['\\'] = "\\\\";
        return escapes;
    }

    private static string UnicodeEscape(char c) =>
        "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);

    private static string? ReadNullableString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : throw new JsonException("The notification hub returned a non-string installation member.");
    }

    /// <summary>Reads the <c>Tags</c> child of the registration description the reader is on.</summary>
    private static async Task<IReadOnlySet<string>> ReadDescriptionTagsAsync(XmlReader reader)
    {
        string? tagsString = null;
        using (var description = reader.ReadSubtree())
        {
            await description.ReadAsync().ConfigureAwait(false);
            var depth = description.Depth;
            while (await description.ReadAsync().ConfigureAwait(false))
            {
                if (description.NodeType == XmlNodeType.Element
                    && description.Depth == depth + 1
                    && description.LocalName == "Tags"
                    && description.NamespaceURI == RegistrationNamespace)
                {
                    tagsString = await description.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    break;
                }
            }
        }

        return ParseTags(tagsString);
    }

    /// <summary>The hub's registration tag rules applied to a comma-separated tag string.</summary>
    private static HashSet<string> ParseTags(string? tagsString)
    {
        if (string.IsNullOrEmpty(tagsString))
        {
            return [with(StringComparer.OrdinalIgnoreCase)];
        }

        var split = tagsString.Split(',');
        var valid = InstallationIdTagRegex.Count(tagsString) <= 1
            && UserIdTagRegex.Count(tagsString) <= 1
            && split.All(tag => SingleTagRegex.IsMatch(tag) && tag.Length <= MaximumTagSize);
        return valid
            ? [with(StringComparer.OrdinalIgnoreCase), .. split]
            : [with(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The <c>Detail</c> of an <c>&lt;Error&gt;&lt;Code/&gt;&lt;Detail/&gt;&lt;/Error&gt;</c> body, or null.</summary>
    private static string? ReadErrorDetail(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var stringReader = new StringReader(responseBody);
            using var reader = XmlReader.Create(stringReader);
            reader.Read();
            reader.ReadStartElement("Error");
            reader.ReadStartElement("Code");
            _ = reader.ReadContentAsString();
            reader.ReadEndElement();
            reader.ReadStartElement("Detail");
            var detail = reader.ReadContentAsString();
            return string.IsNullOrWhiteSpace(detail) ? null : detail;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    // The hub's tag grammar: one plain tag, or an installation or user system tag.
    [GeneratedRegex(@"^(\$(InstallationId|UserId):\{[-_@#.:=\w]+\}|[-_@#.:\w]+)$", TagPatternOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SingleTagRegex { get; }

    [GeneratedRegex(@"\$InstallationId:\{[-_@#.:=\w]+\}", TagPatternOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InstallationIdTagRegex { get; }

    [GeneratedRegex(@"\$UserId:\{[-_@#.:=\w]+\}", TagPatternOptions, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserIdTagRegex { get; }
}
