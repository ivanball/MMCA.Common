using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.UI.Web.Security;

/// <summary>
/// The one test for what counts as a usable API endpoint value, shared by the Content-Security-Policy
/// (which pins <c>connect-src</c> to it), <c>/client-config</c> (which hands <c>Api:WasmApiEndpoint</c>
/// to the browser) and <c>AddMmcaBlazorWebHost</c> (which builds the server head's clients on
/// <c>Api:ApiEndpoint</c>), so a placeholder such as <c>SET-VIA-ENVIRONMENT-VARIABLE</c> is refused the
/// same way everywhere instead of booting a client against a non-URL.
/// </summary>
internal static class ApiEndpointUrl
{
    /// <summary>
    /// Whether <paramref name="value"/> is an absolute <c>http</c> or <c>https</c> URL. The scheme check
    /// matters on Linux, where a rooted path like <c>/relative/path</c> parses as an absolute
    /// <c>file://</c> URI; only an http(s) endpoint is one a browser or an <c>HttpClient</c> can call.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="uri">The parsed URI when the value qualifies.</param>
    /// <returns><see langword="true"/> for an absolute http(s) URL.</returns>
    public static bool TryParseAbsoluteHttp(string? value, [NotNullWhen(true)] out Uri? uri)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && IsHttpScheme(parsed.Scheme))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }

    /// <summary>Whether <paramref name="value"/> is an absolute <c>http</c> or <c>https</c> URL.</summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> for an absolute http(s) URL.</returns>
    public static bool IsAbsoluteHttp([NotNullWhen(true)] string? value) => TryParseAbsoluteHttp(value, out _);

    /// <summary>
    /// Whether <paramref name="value"/> is an absolute http(s) URL OR an Aspire service-discovery URL
    /// whose scheme lists only http and https (<c>https+http://gateway</c>). The second form is valid for
    /// the SERVER head's <c>Api:ApiEndpoint</c> only: every <c>IHttpClientFactory</c> client resolves it
    /// through the service-discovery handler <c>AddServiceDefaults()</c> adds, while a browser never can,
    /// so <c>Api:WasmApiEndpoint</c> uses <see cref="IsAbsoluteHttp"/>.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <returns><see langword="true"/> for an absolute http(s) or service-discovery URL.</returns>
    public static bool IsAbsoluteHttpOrServiceDiscovery([NotNullWhen(true)] string? value)
    {
        if (IsAbsoluteHttp(value))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme.Contains('+', StringComparison.Ordinal)
            && parsed.Scheme.Split('+').All(IsHttpScheme);
    }

    private static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
