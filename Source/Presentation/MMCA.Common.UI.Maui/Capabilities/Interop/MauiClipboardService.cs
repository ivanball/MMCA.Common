using MMCA.Common.UI.Services.Capabilities.Interop;

namespace MMCA.Common.UI.Maui.Capabilities.Interop;

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
