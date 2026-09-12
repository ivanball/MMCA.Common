using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Interfaces.Infrastructure.Storage;

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

    /// <summary>
    /// Uploads (or overwrites) a blob together with the response headers in
    /// <paramref name="options"/>, and returns its public absolute URL. This is the overload a document
    /// upload wants: it is what attaches the <c>Content-Disposition</c> that makes a stored blob come
    /// back as a download under its original file name.
    /// </summary>
    /// <param name="blobName">The blob name within the configured container, e.g. <c>documents/42/deck.pptx</c>.</param>
    /// <param name="content">The blob content, read from the current position.</param>
    /// <param name="contentType">The MIME type stored with the blob.</param>
    /// <param name="options">The response headers to store with the blob; <see cref="FileUploadOptions.None"/> stores none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The blob's absolute URI, or a failure result.</returns>
    /// <remarks>
    /// The default implementation exists only so an implementation written against the original
    /// three-argument contract keeps compiling: it drops <paramref name="options"/> and forwards to
    /// <see cref="UploadAsync(string, Stream, string, CancellationToken)"/>. Implementations SHOULD
    /// override it and honor the headers.
    /// </remarks>
    Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, FileUploadOptions options, CancellationToken cancellationToken = default) =>
        UploadAsync(blobName, content, contentType, cancellationToken);

    /// <summary>Deletes a blob; unknown names succeed (idempotent).</summary>
    /// <param name="blobName">The blob name within the configured container.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or a failure result.</returns>
    Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default);
}
