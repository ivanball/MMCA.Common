namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IClipboardService"/>: clipboard unavailable (reports failure).</summary>
public sealed class NullClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
