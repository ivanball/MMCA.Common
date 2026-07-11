using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Unconfigured file storage (ADR-045). Uploads fail with a clear error so feature endpoints
/// degrade cleanly; deletes succeed (there is nothing to delete). Swapped out by
/// <c>AddAzureBlobFileStorage(configuration)</c>.
/// </summary>
public sealed class NullFileStorageService : IFileStorageService
{
    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<Uri>(Error.Failure(
            code: "FileStorage.NotConfigured",
            message: "No file storage is configured for this host.",
            source: nameof(NullFileStorageService))));

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
