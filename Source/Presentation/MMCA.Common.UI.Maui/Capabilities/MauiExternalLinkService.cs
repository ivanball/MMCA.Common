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
            await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred).ConfigureAwait(false);
        }
        catch (FeatureNotSupportedException)
        {
            // No browser available — swallow; the link is a convenience, not a workflow.
        }
    }
}
