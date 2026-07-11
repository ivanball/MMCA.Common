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

/// <summary>
/// A picked or captured photo. Dispose the stream after upload. Deliberately a class rather
/// than a record: a record's <c>IEquatable&lt;T&gt;</c> is a generic WinRT interface, which
/// trips CsWinRT AOT generation (CsWinRT1030) on the windows TFM of UI.Maui.
/// </summary>
/// <param name="content">The photo bytes, positioned at the start.</param>
/// <param name="fileName">The original or generated file name.</param>
/// <param name="contentType">The MIME type as reported by the platform.</param>
public sealed class PickedMedia(Stream content, string fileName, string contentType) : IDisposable
{
    /// <summary>Gets the photo bytes, positioned at the start.</summary>
    public Stream Content { get; } = content;

    /// <summary>Gets the original or generated file name.</summary>
    public string FileName { get; } = fileName;

    /// <summary>Gets the MIME type as reported by the platform.</summary>
    public string ContentType { get; } = contentType;

    /// <inheritdoc />
    public void Dispose() => Content.Dispose();
}
