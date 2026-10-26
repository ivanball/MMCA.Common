using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Azure.Storage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IFileStorageService"/> (ADR-045). Works
/// against the single container configured in <c>FileStorage:ContainerName</c>; the container
/// itself (and its public-access level) is provisioned by infrastructure, not created here.
/// A service failure (<see cref="RequestFailedException"/>) and a transport failure that never
/// produced a service response (an <see cref="HttpRequestException"/>, an <see cref="IOException"/>,
/// or a timeout after the retry policy, which Azure.Core surfaces as a cancellation the caller did not
/// request) are both a <see cref="Result"/> failure with the <c>FileStorage.UploadFailed</c> /
/// <c>FileStorage.DeleteFailed</c> codes; the caller's own cancellation still propagates.
/// </summary>
public sealed class AzureBlobFileStorageService(
    BlobContainerClient containerClient,
    ILogger<AzureBlobFileStorageService> logger) : IFileStorageService
{
    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Implements the grandfathered IFileStorageService overload pair (see the RS0026 suppression on IFileStorageService.UploadAsync); the type moved here from MMCA.Common.Infrastructure, so both overloads are unshipped in this package and the analyzer flags this one too")]
    public Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        UploadAsync(blobName, content, contentType, FileUploadOptions.None, cancellationToken);

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Grandfathered public overload released after the v1.152 baseline while RS0026/RS0027 were off; changing its signature is a breaking change (RS0026/RS0027 baseline)")]
    public async Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, FileUploadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            await blobClient.UploadAsync(
                content,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = contentType,
                        ContentDisposition = options.ContentDisposition,
                        CacheControl = options.CacheControl,
                    },
                },
                cancellationToken).ConfigureAwait(false);

            return Result.Success(blobClient.Uri);
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Blob upload failed");
            return Result.Failure<Uri>(Error.Failure(
                code: "FileStorage.UploadFailed",
                message: "The file could not be stored.",
                source: nameof(AzureBlobFileStorageService)));
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            logger.LogError(ex, "Blob upload failed");
            return Result.Failure<Uri>(Error.Failure(
                code: "FileStorage.UploadFailed",
                message: "The file could not be stored.",
                source: nameof(AzureBlobFileStorageService)));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        try
        {
            await containerClient.DeleteBlobIfExistsAsync(
                blobName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Blob delete failed");
            return Result.Failure(Error.Failure(
                code: "FileStorage.DeleteFailed",
                message: "The file could not be deleted.",
                source: nameof(AzureBlobFileStorageService)));
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            logger.LogError(ex, "Blob delete failed");
            return Result.Failure(Error.Failure(
                code: "FileStorage.DeleteFailed",
                message: "The file could not be deleted.",
                source: nameof(AzureBlobFileStorageService)));
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
