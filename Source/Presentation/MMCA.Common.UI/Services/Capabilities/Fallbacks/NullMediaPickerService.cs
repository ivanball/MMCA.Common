namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// No-op media picker (ADR-045). Web heads render an <c>InputFile</c> instead of the native
/// picker, so this default only signals "hide the native affordance".
/// </summary>
public sealed class NullMediaPickerService : IMediaPickerService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<PickedMedia?> PickPhotoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PickedMedia?>(null);

    /// <inheritdoc />
    public Task<PickedMedia?> CapturePhotoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PickedMedia?>(null);
}
