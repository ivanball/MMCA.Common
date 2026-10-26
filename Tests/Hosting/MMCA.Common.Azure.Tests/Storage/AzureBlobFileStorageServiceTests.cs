using AwesomeAssertions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Azure.Storage;
using Moq;

namespace MMCA.Common.Azure.Tests.Storage;

/// <summary>
/// M261: the Blob store answers a transport failure the way it answers a service failure, with a
/// <c>FileStorage.UploadFailed</c> / <c>FileStorage.DeleteFailed</c> Result, while the caller's own
/// cancellation keeps propagating. Azure.Core surfaces a network timeout after its retry policy as a
/// cancellation the caller did not request, which is not a <c>RequestFailedException</c>.
/// </summary>
public sealed class AzureBlobFileStorageServiceTests
{
    private readonly Mock<BlobContainerClient> _container = new();
    private readonly Mock<BlobClient> _blob = new();

    public AzureBlobFileStorageServiceTests() =>
        _container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(_blob.Object);

    private AzureBlobFileStorageService CreateSut() =>
        new(_container.Object, NullLogger<AzureBlobFileStorageService>.Instance);

    [Fact]
    public async Task UploadAsync_TimeoutTheCallerDidNotRequest_IsTheUploadFailedResult()
    {
        _blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The operation was cancelled because it exceeded the configured timeout."));
        await using var content = new MemoryStream([1]);

        var result = await CreateSut().UploadAsync("a.png", content, "image/png", FileUploadOptions.None, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.UploadFailed");
    }

    [Fact]
    public async Task UploadAsync_TheCallersOwnCancellation_StillPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await using var content = new MemoryStream([1]);

        var act = () => CreateSut().UploadAsync("a.png", content, "image/png", FileUploadOptions.None, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
