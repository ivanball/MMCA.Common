using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI Essentials media picker (ADR-045, avatar upload). MediaPicker owns the platform
/// permission prompts (photo library on pick, camera on capture); a denied permission,
/// cancelled sheet, or unsupported device all surface as <see langword="null"/>, never an
/// exception.
/// </summary>
public sealed class MauiMediaPickerService : IMediaPickerService
{
    /// <inheritdoc />
    public bool IsSupported => MediaPicker.Default.IsCaptureSupported || DeviceInfo.Current.Platform != DevicePlatform.WinUI;

    /// <inheritdoc />
    public Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default) =>
#pragma warning disable CS0618 // PickPhotoAsync is "obsolete" in favor of the multi-select PickPhotosAsync; an avatar is exactly one photo
        PickCoreAsync(() => MediaPicker.Default.PickPhotoAsync(), cancellationToken);
#pragma warning restore CS0618

    /// <inheritdoc />
    public Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
        MediaPicker.Default.IsCaptureSupported
            ? PickCoreAsync(() => MediaPicker.Default.CapturePhotoAsync(), cancellationToken)
            : Task.FromResult<PickedMedia?>(null);

    private static async Task<PickedMedia?> PickCoreAsync(Func<Task<FileResult?>> pick, CancellationToken cancellationToken)
    {
        try
        {
            var file = await pick().ConfigureAwait(false);
            if (file is null)
            {
                return null;
            }

            var stream = await file.OpenReadAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new PickedMedia(stream, file.FileName, file.ContentType ?? "application/octet-stream");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Do not catch general exception types — picking is best-effort; denied permission = null
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
