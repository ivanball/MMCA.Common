using Microsoft.Extensions.Configuration;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Maui.Services;

/// <summary>
/// MAUI <see cref="IPublicLinkBuilder"/>: shared/copied links must point at the public web app,
/// not the WebView's internal origin, so this builder resolves against the
/// <c>PublicSite:BaseUrl</c> pinned in the head's embedded appsettings (the same mechanism as the
/// gateway endpoint). Register it AFTER <c>AddUIShared</c> (and after any module registration that
/// registers a builder of its own) so it overrides the browser-origin default; last registration
/// wins.
/// </summary>
public sealed class MauiPublicLinkBuilder : IPublicLinkBuilder
{
    /// <summary>Configuration key holding the absolute public site base URL.</summary>
    public const string BaseUrlConfigKey = "PublicSite:BaseUrl";

    private readonly Uri _baseUrl;

    /// <summary>Reads the pinned public site URL from the embedded configuration.</summary>
    /// <param name="configuration">The head's configuration, which must supply <see cref="BaseUrlConfigKey"/>.</param>
    /// <exception cref="InvalidOperationException">The key is missing or blank.</exception>
    public MauiPublicLinkBuilder(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[BaseUrlConfigKey];
        _baseUrl = string.IsNullOrWhiteSpace(configured)
            ? throw new InvalidOperationException(
                $"{BaseUrlConfigKey} is required for shareable links on the MAUI head.")
            : new Uri(configured, UriKind.Absolute);
    }

    /// <inheritdoc />
    public Uri BuildAbsolute(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return new Uri(_baseUrl, relativePath);
    }
}
