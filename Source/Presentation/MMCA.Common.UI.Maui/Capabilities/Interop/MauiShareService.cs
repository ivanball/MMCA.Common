using MMCA.Common.UI.Services.Capabilities.Interop;

namespace MMCA.Common.UI.Maui.Capabilities.Interop;

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The contract reports an unavailable share as false so callers can fall back to
            // copy-link; any platform failure, not only an unsupported feature, means exactly that.
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same contract as ShareLinkAsync: an unsupported feature, an unreadable file or any
            // other platform failure reports false rather than throwing at the caller.
            return false;
        }
    }
}
