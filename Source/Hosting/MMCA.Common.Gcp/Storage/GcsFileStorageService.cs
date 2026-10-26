using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace MMCA.Common.Gcp.Storage;

/// <summary>
/// Google Cloud Storage implementation of <see cref="IFileStorageService"/> (ADR-045) over the JSON
/// API and Application Default Credentials. Works against the single bucket configured in
/// <c>FileStorage:GcsBucket</c>; the bucket itself (and whether it is publicly readable) is
/// provisioned by infrastructure, not created here. Same contract as the Azure Blob and S3
/// implementations: the content type and the <see cref="FileUploadOptions"/> headers are stored with
/// the object, an upload returns the object's URL, a delete of a missing object succeeds, and a
/// service failure is a <see cref="Result"/> failure with the <c>FileStorage.UploadFailed</c> /
/// <c>FileStorage.DeleteFailed</c> codes rather than an exception. So is a transport failure that
/// never produced a service response (an <see cref="HttpRequestException"/>, an
/// <see cref="IOException"/>, or a timeout the caller did not request); the caller's own
/// cancellation still propagates.
/// </summary>
public sealed class GcsFileStorageService(
    StorageClient storageClient,
    IOptions<GcsFileStorageSettings> options,
    ILogger<GcsFileStorageService> logger) : IFileStorageService
{
    private readonly GcsFileStorageSettings _settings = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Implements the grandfathered IFileStorageService overload pair (see the RS0026 suppression on IFileStorageService.UploadAsync); both overloads are unshipped in this new package and the analyzer flags this one too, exactly as on AzureBlobFileStorageService and S3FileStorageService")]
    public Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        UploadAsync(blobName, content, contentType, FileUploadOptions.None, cancellationToken);

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Grandfathered public overload released after the v1.152 baseline while RS0026/RS0027 were off; changing its signature is a breaking change (RS0026/RS0027 baseline)")]
    public async Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, FileUploadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Composed before the upload so a configuration that cannot name the object never leaves an
        // orphan behind it.
        var objectUri = GcsObjectUrl.Compose(_settings, blobName);

        // The headers are object metadata on Cloud Storage: they are written with the object and
        // served back on every read of it.
        var destination = new StorageObject
        {
            Bucket = _settings.GcsBucket,
            Name = blobName,
            ContentType = contentType,
            ContentDisposition = options.ContentDisposition is { Length: > 0 } contentDisposition ? contentDisposition : null,
            CacheControl = options.CacheControl is { Length: > 0 } cacheControl ? cacheControl : null,
        };

        try
        {
            // The client reads the stream and leaves it open: the caller owns it, exactly as with
            // the Blob client.
            await storageClient.UploadObjectAsync(destination, content, options: null, cancellationToken).ConfigureAwait(false);
            return Result.Success(objectUri);
        }
        catch (GoogleApiException ex)
        {
            logger.LogError(ex, "Cloud Storage object upload failed");
            return Result.Failure<Uri>(Error.Failure(
                code: "FileStorage.UploadFailed",
                message: "The file could not be stored.",
                source: nameof(GcsFileStorageService)));
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            logger.LogError(ex, "Cloud Storage object upload failed");
            return Result.Failure<Uri>(Error.Failure(
                code: "FileStorage.UploadFailed",
                message: "The file could not be stored.",
                source: nameof(GcsFileStorageService)));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        try
        {
            await storageClient.DeleteObjectAsync(_settings.GcsBucket, blobName, options: null, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Cloud Storage answers 404 for a missing object; the contract is delete-if-exists, the
            // DeleteBlobIfExists semantics of the Blob store.
            return Result.Success();
        }
        catch (GoogleApiException ex)
        {
            logger.LogError(ex, "Cloud Storage object delete failed");
            return Result.Failure(Error.Failure(
                code: "FileStorage.DeleteFailed",
                message: "The file could not be deleted.",
                source: nameof(GcsFileStorageService)));
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            logger.LogError(ex, "Cloud Storage object delete failed");
            return Result.Failure(Error.Failure(
                code: "FileStorage.DeleteFailed",
                message: "The file could not be deleted.",
                source: nameof(GcsFileStorageService)));
        }
    }

    /// <summary>
    /// A failure that never produced a service response: a connect or DNS failure, a dropped
    /// connection, or a timeout after the client's own retries (surfaced as an
    /// <see cref="OperationCanceledException"/> the caller did not request). The caller's own
    /// cancellation is not one and keeps propagating.
    /// </summary>
    private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
