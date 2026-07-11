namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Captures the current app screen to a temporary image file, for pairing with
/// <see cref="IShareService.ShareFileAsync"/> ("share my schedule as image"). Files land in
/// the platform cache directory — never the photo library — so no storage permissions are needed.
/// </summary>
public interface IScreenshotService
{
    /// <summary>Whether screen capture is available on this platform (web/null fallbacks: <see langword="false"/>).</summary>
    bool IsSupported { get; }

    /// <summary>Captures the screen to a temp PNG and returns its path, or <see langword="null"/> on failure.</summary>
    Task<string?> CaptureToFileAsync(CancellationToken cancellationToken = default);
}
