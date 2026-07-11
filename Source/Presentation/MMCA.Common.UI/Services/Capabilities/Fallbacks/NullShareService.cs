namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IShareService"/>: sharing unavailable; callers fall back to copy-link.</summary>
public sealed class NullShareService : IShareService
{
    /// <inheritdoc />
    public Task<bool> ShareLinkAsync(string title, Uri uri, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> ShareFileAsync(string title, string filePath, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
