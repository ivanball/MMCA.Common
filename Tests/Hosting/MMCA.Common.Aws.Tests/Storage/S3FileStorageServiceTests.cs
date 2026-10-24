using System.Net;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Aws.Storage;
using Moq;

namespace MMCA.Common.Aws.Tests.Storage;

/// <summary>
/// The S3 store's contract against a mocked client: the request it sends (bucket, key, content
/// type, the <see cref="FileUploadOptions"/> headers, stream ownership, payload signing) and the
/// Result it returns, with the same error codes as the Azure Blob store. The real wire round trip
/// lives in the LocalStack tier.
/// </summary>
public sealed class S3FileStorageServiceTests
{
    private readonly Mock<IAmazonS3> _s3 = new();

    public S3FileStorageServiceTests() =>
        _s3.SetupGet(s => s.Config).Returns(new AmazonS3Config { RegionEndpoint = RegionEndpoint.EUWest1 });

    private S3FileStorageService CreateSut(S3FileStorageSettings? settings = null) =>
        new(
            _s3.Object,
            Options.Create(settings ?? new S3FileStorageSettings { Bucket = "avatars", Region = "eu-west-1" }),
            NullLogger<S3FileStorageService>.Instance);

    [Fact]
    public async Task UploadAsync_StoresTheObjectWithItsHeaders_AndReturnsItsUrl()
    {
        PutObjectRequest? captured = null;
        _s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        await using var content = new MemoryStream([1, 2, 3]);

        var result = await CreateSut().UploadAsync(
            "users/42/report.pdf",
            content,
            "application/pdf",
            FileUploadOptions.Attachment("report.pdf", "private, max-age=60"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new Uri("https://avatars.s3.eu-west-1.amazonaws.com/users/42/report.pdf"));
        captured.Should().NotBeNull();
        captured!.BucketName.Should().Be("avatars");
        captured.Key.Should().Be("users/42/report.pdf");
        captured.ContentType.Should().Be("application/pdf");
        captured.Headers.ContentDisposition.Should().StartWith("attachment; filename=\"report.pdf\"");
        captured.Headers.CacheControl.Should().Be("private, max-age=60");
        captured.InputStream.Should().BeSameAs(content);
        captured.AutoCloseStream.Should().BeFalse("the caller owns the stream, as with the Blob client");
        captured.DisablePayloadSigning.Should().BeNull("AWS S3 itself keeps the SDK's signing default");
        content.CanRead.Should().BeTrue();
    }

    [Fact]
    public async Task UploadAsync_WithoutOptions_SendsNoDispositionOrCacheControl()
    {
        PutObjectRequest? captured = null;
        _s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse());
        await using var content = new MemoryStream([1]);

        var result = await CreateSut().UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        captured!.Headers.ContentDisposition.Should().BeNullOrEmpty();
        captured.Headers.CacheControl.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://acct.r2.cloudflarestorage.com", true)]
    [InlineData("http://localhost:4566", false)]
    public async Task UploadAsync_SendsAnUnsignedPayload_OnlyToAnHttpsCompatibleEndpoint(string serviceUrl, bool unsignedPayload)
    {
        PutObjectRequest? captured = null;
        _s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new PutObjectResponse());
        await using var content = new MemoryStream([1]);
        var sut = CreateSut(new S3FileStorageSettings { Bucket = "b", ServiceUrl = new Uri(serviceUrl), ForcePathStyle = true });

        await sut.UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        captured!.DisablePayloadSigning.GetValueOrDefault().Should().Be(unsignedPayload);
    }

    [Fact]
    public async Task UploadAsync_ServiceFailure_IsTheUploadFailedResult()
    {
        _s3.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Access Denied"));
        await using var content = new MemoryStream([1]);

        var result = await CreateSut().UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.UploadFailed");
    }

    [Fact]
    public async Task UploadAsync_RejectsNullOptions()
    {
        await using var content = new MemoryStream([1]);

        var act = () => CreateSut().UploadAsync("a.png", content, "image/png", null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeleteAsync_DeletesTheKeyFromTheBucket()
    {
        DeleteObjectRequest? captured = null;
        _s3.Setup(s => s.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteObjectRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent });

        var result = await CreateSut().DeleteAsync("users/42/a.png", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        captured!.BucketName.Should().Be("avatars");
        captured.Key.Should().Be("users/42/a.png");
    }

    [Fact]
    public async Task DeleteAsync_ServiceFailure_IsTheDeleteFailedResult()
    {
        _s3.Setup(s => s.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Access Denied"));

        var result = await CreateSut().DeleteAsync("a.png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.DeleteFailed");
    }

    [Fact]
    public void Constructor_RejectsNullOptions()
    {
        var act = () => new S3FileStorageService(_s3.Object, null!, NullLogger<S3FileStorageService>.Instance);

        act.Should().Throw<ArgumentNullException>();
    }
}
