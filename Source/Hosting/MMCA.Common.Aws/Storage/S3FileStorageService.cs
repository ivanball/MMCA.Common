using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Aws.Storage;

/// <summary>
/// S3-compatible implementation of <see cref="IFileStorageService"/> (ADR-045): AWS S3, Cloudflare
/// R2, MinIO and the GCS XML API through one client. Works against the single bucket configured in
/// <c>FileStorage:Bucket</c>; the bucket itself (and whether it is publicly readable) is provisioned
/// by infrastructure, not created here. Same contract as the Azure Blob implementation: the content
/// type and the <see cref="FileUploadOptions"/> headers are stored with the object, an upload returns
/// the object's URL, a delete of a missing object succeeds, and a service failure is a
/// <see cref="Result"/> failure with the <c>FileStorage.UploadFailed</c> / <c>FileStorage.DeleteFailed</c>
/// codes rather than an exception.
/// </summary>
public sealed class S3FileStorageService(
    IAmazonS3 s3Client,
    IOptions<S3FileStorageSettings> options,
    ILogger<S3FileStorageService> logger) : IFileStorageService
{
    private readonly S3FileStorageSettings _settings = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Implements the grandfathered IFileStorageService overload pair (see the RS0026 suppression on IFileStorageService.UploadAsync); both overloads are unshipped in this new package and the analyzer flags this one too, exactly as on AzureBlobFileStorageService")]
    public Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        UploadAsync(blobName, content, contentType, FileUploadOptions.None, cancellationToken);

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Grandfathered public overload released after the v1.152 baseline while RS0026/RS0027 were off; changing its signature is a breaking change (RS0026/RS0027 baseline)")]
    public async Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, FileUploadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Composed before the upload so a configuration that cannot name the object never leaves an
        // orphan behind it.
        var objectUri = S3ObjectUrl.Compose(_settings, s3Client.Config.RegionEndpoint?.SystemName, blobName);

        var request = new PutObjectRequest
        {
            BucketName = _settings.Bucket,
            Key = blobName,
            InputStream = content,
            ContentType = contentType,

            // The caller owns the stream, exactly as with the Blob client.
            AutoCloseStream = false,
        };

        if (SendsUnsignedPayload(_settings))
        {
            request.DisablePayloadSigning = true;
        }

        if (options.ContentDisposition is { Length: > 0 } contentDisposition)
        {
            request.Headers.ContentDisposition = contentDisposition;
        }

        if (options.CacheControl is { Length: > 0 } cacheControl)
        {
            request.Headers.CacheControl = cacheControl;
        }

        try
        {
            await s3Client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return Result.Success(objectUri);
        }
        catch (AmazonServiceException ex)
        {
            logger.LogError(ex, "S3 object upload failed");
            return Result.Failure<Uri>(Error.Failure(
                code: "FileStorage.UploadFailed",
                message: "The file could not be stored.",
                source: nameof(S3FileStorageService)));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        try
        {
            // S3 DeleteObject is already idempotent (a missing key answers 204), which is the
            // DeleteBlobIfExists semantics the contract asks for.
            await s3Client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = _settings.Bucket, Key = blobName },
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (AmazonServiceException ex)
        {
            logger.LogError(ex, "S3 object delete failed");
            return Result.Failure(Error.Failure(
                code: "FileStorage.DeleteFailed",
                message: "The file could not be deleted.",
                source: nameof(S3FileStorageService)));
        }
    }

    /// <summary>
    /// Whether an upload is signed with <c>UNSIGNED-PAYLOAD</c> instead of the SDK's default streaming
    /// SigV4 chunked encoding: only for an S3-compatible endpoint (<c>ServiceUrl</c>) reached over
    /// HTTPS. Cloudflare R2 does not implement the streaming signature (Cloudflare's guidance for the
    /// .NET SDK is exactly this flag), and TLS already protects the body in transit. AWS S3 itself keeps
    /// the SDK default, and a plain-HTTP endpoint (MinIO, LocalStack) must keep it, because the SDK
    /// refuses an unsigned payload over HTTP.
    /// </summary>
    /// <param name="settings">The bound settings.</param>
    /// <returns><see langword="true"/> for an HTTPS <c>ServiceUrl</c>.</returns>
    internal static bool SendsUnsignedPayload(S3FileStorageSettings settings) =>
        settings.ServiceUrl is { IsAbsoluteUri: true } serviceUrl
        && string.Equals(serviceUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
