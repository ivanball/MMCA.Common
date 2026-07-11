using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IFileStorageService"/> (ADR-045). Works
/// against the single container configured in <c>FileStorage:ContainerName</c>; the container
/// itself (and its public-access level) is provisioned by infrastructure, not created here.
/// </summary>
public sealed class AzureBlobFileStorageService(
    BlobContainerClient containerClient,
    ILogger<AzureBlobFileStorageService> logger) : IFileStorageService
{
    /// <inheritdoc />
    public bool IsConfigured => true;

    /// <inheritdoc />
    public async Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var blobClient = containerClient.GetBlobClient(blobName);
            await blobClient.UploadAsync(
                content,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
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
    }
}
