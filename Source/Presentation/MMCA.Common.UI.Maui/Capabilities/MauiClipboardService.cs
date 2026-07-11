using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>MAUI <see cref="IClipboardService"/> over <c>Clipboard.Default</c>.</summary>
public sealed class MauiClipboardService : IClipboardService
{
    /// <inheritdoc />
    public async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(text).ConfigureAwait(false);
            return true;
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
    }
}
