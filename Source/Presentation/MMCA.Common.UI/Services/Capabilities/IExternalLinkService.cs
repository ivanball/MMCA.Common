namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Opens URLs outside the current UI surface. In browsers this is a new tab; inside a
/// BlazorWebView it launches the system browser — <c>target="_blank"</c> silently dead-ends
/// in WKWebView, so shared components must route external links through this service
/// (via the <c>ExternalLink</c> component) instead of raw anchor targets.
/// </summary>
public interface IExternalLinkService
{
    /// <summary>
    /// Whether links must be intercepted and opened through <see cref="OpenAsync"/>
    /// (<see langword="true"/> in native WebView hosts). When <see langword="false"/>,
    /// components may render a plain anchor with <c>target="_blank"</c>.
    /// </summary>
    bool InterceptsLinks { get; }

    /// <summary>Opens <paramref name="uri"/> in the system browser / a new tab. Best-effort.</summary>
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}
