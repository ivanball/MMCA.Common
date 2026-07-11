using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IScreenshotService"/> over <c>Screenshot.Default</c>. Captures land in
/// the platform cache directory (never the photo library), so no storage permissions apply
/// and the OS reclaims the files.
/// </summary>
public sealed class MauiScreenshotService : IScreenshotService
{
    /// <inheritdoc />
    public bool IsSupported => Screenshot.Default.IsCaptureSupported;

    /// <inheritdoc />
    public async Task<string?> CaptureToFileAsync(CancellationToken cancellationToken = default)
    {
        if (!Screenshot.Default.IsCaptureSupported)
        {
            return null;
        }

        try
        {
            var capture = await Screenshot.Default.CaptureAsync().ConfigureAwait(false);
            var path = Path.Combine(FileSystem.CacheDirectory, $"mmca-screenshot-{Guid.NewGuid():N}.png");

            var sourceStream = await capture.OpenReadAsync(ScreenshotFormat.Png).ConfigureAwait(false);
            await using (sourceStream.ConfigureAwait(false))
            {
                var fileStream = File.Create(path);
                await using (fileStream.ConfigureAwait(false))
                {
                    await sourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }
            }

            return path;
        }
        catch (FeatureNotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
