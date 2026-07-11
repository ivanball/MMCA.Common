using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>MAUI <see cref="IShareService"/> over the native share sheet (<c>Share.Default</c>).</summary>
public sealed class MauiShareService : IShareService
{
    /// <inheritdoc />
    public async Task<bool> ShareLinkAsync(string title, Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            await Share.Default.RequestAsync(new ShareTextRequest
            {
                Title = title,
                Uri = uri.ToString(),
            }).ConfigureAwait(false);
            return true;
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ShareFileAsync(string title, string filePath, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        try
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = title,
                File = new ShareFile(filePath, contentType),
            }).ConfigureAwait(false);
            return true;
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
