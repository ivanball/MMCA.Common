using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"Api"</c> configuration section.
/// Validated at startup via <c>ValidateDataAnnotations</c> to fail fast when the endpoint is missing.
/// </summary>
public sealed class ApiSettings : IApiSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "Api";

    /// <summary>Base URL of the WebAPI (e.g., <c>https://localhost:6001</c>).</summary>
    [Required]
    public string? ApiEndpoint { get; init; }

    /// <inheritdoc />
    public string? WasmApiEndpoint { get; init; }

    /// <summary>
    /// Absolute base URL of the UI host's same-origin API proxy (for example
    /// <c>https://app.example.com/api/</c>), set only on a WebAssembly client whose Server host opted
    /// in with <c>AddCommonSameOriginApiProxy</c>. The host serves it as an origin-relative path in
    /// <c>/client-config</c> and <c>MmcaClientConfigBootstrap.LoadAsync</c> resolves it against the
    /// app's base address. When set, the <c>"APIClient"</c> and the notification hub target this
    /// address instead of <see cref="ApiEndpoint"/>, send no <c>Authorization</c> header (the proxy
    /// attaches the bearer from the HttpOnly session cookie server-side) and add the proxy's CSRF
    /// header. <see cref="ApiEndpoint"/> keeps the gateway URL for full-page navigations that must
    /// reach the gateway itself (the external sign-in challenge). <see langword="null"/> everywhere
    /// else, which leaves every client exactly as it was.
    /// </summary>
    public string? SameOriginApiEndpoint { get; init; }
}
