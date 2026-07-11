namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Picks or captures a photo on native heads (ADR-045, avatar upload). Implementations own the
/// permission flow (photo library / camera) and never throw; a denied permission or cancelled
/// picker returns <see langword="null"/>. Web heads keep the null default and render a plain
/// <c>InputFile</c> instead - the affordance switch, not a degraded path.
/// </summary>
public interface IMediaPickerService
{
    /// <summary>Whether native photo picking is available on this head.</summary>
    bool IsSupported { get; }

    /// <summary>Opens the photo picker. Returns <see langword="null"/> when cancelled or unavailable.</summary>
    Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the camera to capture a photo. Returns <see langword="null"/> when cancelled, denied, or unavailable.</summary>
    Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default);
}

/// <summary>A picked or captured photo. Dispose the stream after upload.</summary>
/// <param name="Content">The photo bytes, positioned at the start.</param>
/// <param name="FileName">The original or generated file name.</param>
/// <param name="ContentType">The MIME type as reported by the platform.</param>
public sealed record PickedMedia(Stream Content, string FileName, string ContentType) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Content.Dispose();
}
