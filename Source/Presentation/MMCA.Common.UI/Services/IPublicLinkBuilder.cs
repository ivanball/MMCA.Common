namespace MMCA.Common.UI.Services;

/// <summary>
/// Builds absolute, publicly shareable web URLs for app routes (share sheet, copy-link, QR).
/// Web heads derive them from the browser origin; the MAUI head cannot (its internal origin is
/// the WebView's virtual host), so it substitutes the configured public site base URL and the
/// shared pages stay head-agnostic.
/// </summary>
public interface IPublicLinkBuilder
{
    /// <summary>Returns the absolute public URL for an app-relative path (e.g. <c>/sessions/42</c>).</summary>
    /// <param name="relativePath">The app-relative route to make absolute.</param>
    Uri BuildAbsolute(string relativePath);
}
