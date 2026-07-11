using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Stores and deletes binary blobs (ADR-045), e.g. user avatar images. Implementations own the
/// container/bucket; callers pass only a blob name scoped within it. The default implementation
/// is unconfigured (uploads fail with a clear error) until a host calls
/// <c>AddAzureBlobFileStorage(configuration)</c> with a complete <c>FileStorage</c> section.
/// </summary>
public interface IFileStorageService
{
    /// <summary>Whether a real store is configured. Handlers can gate features on this.</summary>
    bool IsConfigured { get; }

    /// <summary>Uploads (or overwrites) a blob and returns its public absolute URL.</summary>
    /// <param name="blobName">The blob name within the configured container, e.g. <c>avatars/42-a1b2c3d4.jpg</c>.</param>
    /// <param name="content">The blob content, read from the current position.</param>
    /// <param name="contentType">The MIME type stored with the blob.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The blob's absolute URI, or a failure result.</returns>
    Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Deletes a blob; unknown names succeed (idempotent).</summary>
    /// <param name="blobName">The blob name within the configured container.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or a failure result.</returns>
    Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default);
}
