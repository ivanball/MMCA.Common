namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IScreenshotService"/>: capture unavailable.</summary>
public sealed class NullScreenshotService : IScreenshotService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<string?> CaptureToFileAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
