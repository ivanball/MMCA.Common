using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IExternalLinkService"/>: intercepts external links
/// (<see cref="InterceptsLinks"/> is <see langword="true"/> because <c>target="_blank"</c>
/// dead-ends inside a BlazorWebView) and opens them in the system browser.
/// </summary>
public sealed class MauiExternalLinkService : IExternalLinkService
{
    /// <inheritdoc />
    public bool InterceptsLinks => true;

    /// <inheritdoc />
    public async Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred).ConfigureAwait(false);
                return;
            }

            // Browser.Default only accepts http(s); everything else (mailto:, tel:, sms:) goes
            // to the OS handler via the launcher so contact links work inside the WebView.
            await Launcher.Default.TryOpenAsync(uri).ConfigureAwait(false);
        }
        catch (FeatureNotSupportedException)
        {
            // No handler available — swallow; the link is a convenience, not a workflow.
        }
    }
}
