namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Writes text to the system clipboard (MAUI <c>Clipboard.Default</c>,
/// browser <c>navigator.clipboard</c>). Returns success so callers can confirm with a snackbar.
/// </summary>
public interface IClipboardService
{
    /// <summary>Copies <paramref name="text"/> to the clipboard. Returns whether the write succeeded.</summary>
    Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default);
}
