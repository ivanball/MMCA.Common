using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// The parsed <c>NativePush</c> connection string and hub name, plus the Shared Access Signature
/// that authorizes every request to the hub (ADR-044). The parsing, the request URI shape and the
/// token algorithm follow the Notification Hubs REST contract exactly as the retired
/// Microsoft.Azure.NotificationHubs 4.2.0 SDK sent it, so the hub sees the same traffic.
/// </summary>
internal sealed partial class NotificationHubConnection
{
    /// <summary>The REST API version every request carries.</summary>
    internal const string ApiVersion = "2020-06";

    /// <summary>How long a signed token stays valid.</summary>
    internal static readonly TimeSpan TokenTimeToLive = TimeSpan.FromMinutes(20);

    private const string EndpointKey = "Endpoint";
    private const string SharedAccessKeyNameKey = "SharedAccessKeyName";
    private const string SharedAccessKeyKey = "SharedAccessKey";

    private readonly byte[] _encodedSharedAccessKey;

    private NotificationHubConnection(Uri endpoint, string keyName, string sharedAccessKey, string hubName)
    {
        Endpoint = endpoint;
        KeyName = keyName;
        HubName = hubName;
        _encodedSharedAccessKey = Encoding.UTF8.GetBytes(sharedAccessKey);
    }

    /// <summary>The namespace endpoint (<c>sb://{namespace}.servicebus.windows.net/</c>).</summary>
    internal Uri Endpoint { get; }

    /// <summary>The shared access policy name.</summary>
    internal string KeyName { get; }

    /// <summary>The notification hub name within the namespace.</summary>
    internal string HubName { get; }

    /// <summary>
    /// Parses a namespace connection string (<c>Endpoint=...;SharedAccessKeyName=...;SharedAccessKey=...</c>).
    /// The three keys match case-insensitively, a value runs to the next recognized key, and any other
    /// key is rejected, as the hub's own connection string parser does.
    /// </summary>
    /// <exception cref="FormatException">The connection string is malformed or misses a key.</exception>
    internal static NotificationHubConnection Parse(string connectionString, string hubName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(hubName);

        // Splitting ";" + connectionString on ";(Key)=" yields { "", key1, value1, key2, value2, ... }.
        var parts = KeyDelimiterRegex.Split(";" + connectionString);
        if (!string.IsNullOrWhiteSpace(parts[0]) || parts.Length % 2 != 1)
        {
            throw new FormatException("The NativePush connection string is not in the Endpoint=...;SharedAccessKeyName=...;SharedAccessKey=... format.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < parts.Length; i += 2)
        {
            var key = parts[i];
            var value = parts[i + 1];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FormatException($"The NativePush connection string has an empty {key} value.");
            }

            if (!values.TryAdd(key, value))
            {
                throw new FormatException($"The NativePush connection string sets {key} more than once.");
            }
        }

        if (!values.TryGetValue(EndpointKey, out var endpoint)
            || !values.TryGetValue(SharedAccessKeyNameKey, out var keyName)
            || !values.TryGetValue(SharedAccessKeyKey, out var sharedAccessKey))
        {
            throw new FormatException("The NativePush connection string must carry Endpoint, SharedAccessKeyName and SharedAccessKey.");
        }

        return new NotificationHubConnection(new Uri(endpoint), keyName, sharedAccessKey, hubName);
    }

    /// <summary>
    /// A request URI builder rooted at <c>https://{namespace}/{hub}/?api-version=2020-06</c>; callers
    /// append the operation path and any further query parameters.
    /// </summary>
    internal UriBuilder CreateRequestUriBuilder()
    {
        var uriBuilder = new UriBuilder(Endpoint)
        {
            Scheme = Uri.UriSchemeHttps,
        };

        if (uriBuilder.Path is not [.., '/'])
        {
            uriBuilder.Path += "/";
        }

        uriBuilder.Path = $"{uriBuilder.Path}{HubName}/";
        AddToQuery(uriBuilder, "api-version=" + ApiVersion);
        return uriBuilder;
    }

    /// <summary>
    /// Appends a raw query fragment (callers include the leading <c>&amp;</c> after the first). The
    /// fragment is not encoded, matching the hub contract the client reproduces.
    /// </summary>
    internal static void AddToQuery(UriBuilder uriBuilder, string query) =>
        uriBuilder.Query = string.IsNullOrEmpty(uriBuilder.Query)
            ? query
            : uriBuilder.Query[1..] + query;

    /// <summary>
    /// The <c>Authorization</c> header value for a request to <paramref name="requestUri"/>:
    /// <c>SharedAccessSignature sr={audience}&amp;sig={signature}&amp;se={expiry}&amp;skn={keyName}</c>.
    /// The audience is the request URI on the http scheme with no port, query or fragment and a
    /// trailing slash, lower-cased and URL-encoded; the signature is the Base64 HMAC-SHA256 of
    /// <c>{audience}\n{expiry}</c> under the UTF-8 key; the expiry is Unix seconds, twenty minutes out.
    /// </summary>
    internal string CreateSharedAccessSignature(Uri requestUri, DateTimeOffset utcNow)
    {
#pragma warning disable CA1308 // The hub verifies the signature over the LOWER-cased audience.
        var audience = HttpUtility.UrlEncode(NormalizeAudience(requestUri.ToString()).ToLowerInvariant());
#pragma warning restore CA1308
        var expiresOn = Convert.ToInt64(
            (utcNow.UtcDateTime + TokenTimeToLive - DateTime.UnixEpoch).TotalSeconds,
            CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

        string signature;
        using (var hmac = new HMACSHA256(_encodedSharedAccessKey))
        {
            signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(audience + "\n" + expiresOn)));
        }

        return $"SharedAccessSignature sr={audience}&sig={HttpUtility.UrlEncode(signature)}&se={HttpUtility.UrlEncode(expiresOn)}&skn={HttpUtility.UrlEncode(KeyName)}";
    }

    private static string NormalizeAudience(string uri)
    {
        var uriBuilder = new UriBuilder(uri)
        {
            Scheme = "http",
            Port = -1,
            Fragment = string.Empty,
            Password = string.Empty,
            UserName = string.Empty,
            Query = string.Empty,
        };

        if (uriBuilder.Path is not [.., '/'])
        {
            uriBuilder.Path += "/";
        }

        return uriBuilder.Uri.AbsoluteUri;
    }

    // The named group is captured, so Regex.Split keeps each key between the values.
    [GeneratedRegex(
        ";(?<key>Endpoint|SharedAccessKeyName|SharedAccessKey)=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyDelimiterRegex { get; }
}
