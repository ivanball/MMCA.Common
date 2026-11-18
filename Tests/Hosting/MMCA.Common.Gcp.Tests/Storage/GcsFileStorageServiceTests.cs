using System.Net;
using AwesomeAssertions;
using Google;
using Google.Apis.Upload;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Gcp.Storage;
using MMCA.Common.Tests.Shared.Hosting;
using Moq;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace MMCA.Common.Gcp.Tests.Storage;

/// <summary>
/// The Cloud Storage store's contract against a mocked client: the object it uploads (bucket, name,
/// content type, the <see cref="FileUploadOptions"/> headers as object metadata, stream ownership),
/// the delete-if-exists semantics, and the Result it returns, with the same error codes as the Azure
/// Blob and S3 stores. The failure cases shared with the S3 store run from
/// <see cref="FileStorageServiceFailureTestsBase"/>; the cases below are Cloud Storage-only. The real
/// wire round trip lives in the emulator tier.
/// </summary>
public sealed class GcsFileStorageServiceTests : FileStorageServiceFailureTestsBase
{
    private readonly Mock<StorageClient> _client = new();

    private GcsFileStorageService CreateSut(GcsFileStorageSettings? settings) =>
        new(
            _client.Object,
            Options.Create(settings ?? new GcsFileStorageSettings { GcsBucket = "avatars" }),
            NullLogger<GcsFileStorageService>.Instance);

    protected override IFileStorageService CreateSut() => CreateSut(settings: null);

    protected override void ConstructWithNullOptions() =>
        _ = new GcsFileStorageService(_client.Object, null!, NullLogger<GcsFileStorageService>.Instance);

    protected override Exception CreateServiceException() =>
        new GoogleApiException("storage", "Forbidden") { HttpStatusCode = HttpStatusCode.Forbidden };

    protected override void FailUploadWith(Exception exception) =>
        _client.Setup(c => c.UploadObjectAsync(
                It.IsAny<StorageObject>(),
                It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<IUploadProgress>>()))
            .ThrowsAsync(exception);

    protected override void FailDeleteWith(Exception exception) =>
        _client.Setup(c => c.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

    private void SetupUploadSucceeds() =>
        _client.Setup(c => c.UploadObjectAsync(
                It.IsAny<StorageObject>(),
                It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<IUploadProgress>>()))
            .ReturnsAsync(new StorageObject());

    [Fact]
    public async Task UploadAsync_StoresTheObjectWithItsHeaders_AndReturnsItsUrl()
    {
        StorageObject? captured = null;
        Stream? sentStream = null;
        _client.Setup(c => c.UploadObjectAsync(
                It.IsAny<StorageObject>(),
                It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<IUploadProgress>>()))
            .Callback<StorageObject, Stream, UploadObjectOptions, CancellationToken, IProgress<IUploadProgress>>((destination, source, _, _, _) =>
            {
                captured = destination;
                sentStream = source;
            })
            .ReturnsAsync(new StorageObject());
        await using var content = new MemoryStream([1, 2, 3]);

        var result = await CreateSut().UploadAsync(
            "users/42/report.pdf",
            content,
            "application/pdf",
            FileUploadOptions.Attachment("report.pdf", "private, max-age=60"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new Uri("https://storage.googleapis.com/avatars/users/42/report.pdf"));
        captured.Should().NotBeNull();
        captured!.Bucket.Should().Be("avatars");
        captured.Name.Should().Be("users/42/report.pdf");
        captured.ContentType.Should().Be("application/pdf");
        captured.ContentDisposition.Should().Be(FileUploadOptions.Attachment("report.pdf").ContentDisposition);
        captured.CacheControl.Should().Be("private, max-age=60");
        sentStream.Should().BeSameAs(content);
        content.CanRead.Should().BeTrue("the caller owns the stream, as with the Blob client");
    }

    [Fact]
    public async Task UploadAsync_WithoutOptions_StoresNoDispositionOrCacheControl()
    {
        StorageObject? captured = null;
        _client.Setup(c => c.UploadObjectAsync(
                It.IsAny<StorageObject>(),
                It.IsAny<Stream>(),
                It.IsAny<UploadObjectOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<IProgress<IUploadProgress>>()))
            .Callback<StorageObject, Stream, UploadObjectOptions, CancellationToken, IProgress<IUploadProgress>>((destination, _, _, _, _) => captured = destination)
            .ReturnsAsync(new StorageObject());
        await using var content = new MemoryStream([1]);

        var result = await CreateSut().UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        captured!.ContentType.Should().Be("image/png");
        captured.ContentDisposition.Should().BeNull();
        captured.CacheControl.Should().BeNull();
    }

    [Fact]
    public async Task UploadAsync_ReturnsThePublicBaseUrlForm_WithTheNameEscaped()
    {
        SetupUploadSucceeds();
        await using var content = new MemoryStream([1]);
        var sut = CreateSut(new GcsFileStorageSettings { GcsBucket = "avatars", GcsPublicBaseUrl = new Uri("https://cdn.example.com/assets/") });

        var result = await sut.UploadAsync("users/42/q1 report.pdf", content, "application/pdf", TestContext.Current.CancellationToken);

        result.Value.Should().Be(new Uri("https://cdn.example.com/assets/users/42/q1%20report.pdf"));
    }

    [Fact]
    public async Task DeleteAsync_DeletesTheObjectFromTheBucket()
    {
        string? bucket = null;
        string? name = null;
        _client.Setup(c => c.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DeleteObjectOptions, CancellationToken>((b, n, _, _) =>
            {
                bucket = b;
                name = n;
            })
            .Returns(Task.CompletedTask);

        var result = await CreateSut().DeleteAsync("users/42/a.png", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        bucket.Should().Be("avatars");
        name.Should().Be("users/42/a.png");
    }

    [Fact]
    public async Task DeleteAsync_OfAMissingObject_Succeeds()
    {
        _client.Setup(c => c.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GoogleApiException("storage", "No such object") { HttpStatusCode = HttpStatusCode.NotFound });

        var result = await CreateSut().DeleteAsync("gone.png", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("delete-if-exists semantics, as with the Blob and S3 stores");
    }

    // M261: a timeout the caller did not request is a transport failure, not an escaping exception.
    [Fact]
    public async Task DeleteAsync_TimeoutTheCallerDidNotRequest_IsTheDeleteFailedResult()
    {
        _client.Setup(c => c.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request timed out."));

        var result = await CreateSut().DeleteAsync("a.png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.DeleteFailed");
    }

    [Fact]
    public async Task DeleteAsync_TheCallersOwnCancellation_StillPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _client.Setup(c => c.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeleteObjectOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var act = () => CreateSut().DeleteAsync("a.png", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
