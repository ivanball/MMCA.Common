using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// The shipped S3 store, registered through <c>AddS3FileStorage</c> exactly as a host would, against
/// LocalStack's S3 API: the request the adapter builds (checksum posture <c>WHEN_REQUIRED</c>,
/// path-style addressing, streaming SigV4 over plain HTTP) is accepted, the headers come back on the
/// object, the returned URL addresses it, and a delete is idempotent.
/// </summary>
public sealed class S3FileStorageLocalStackTests(LocalStackFixture localStack) : IAsyncLifetime
{
    private readonly string _bucket = $"mmca-{Guid.NewGuid():N}";
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddS3FileStorage(LocalStackFixture.ConfigurationWith(localStack.FileStorageSettings(_bucket)));
        _provider = services.BuildServiceProvider();

        // The adapter never creates its bucket (infrastructure provisions it), so the test does.
        await S3.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private IAmazonS3 S3 => _provider.GetRequiredService<IAmazonS3>();

    private IFileStorageService Sut => _provider.GetRequiredService<IFileStorageService>();

    [Fact]
    public async Task Upload_StoresTheBytesAndHeaders_AtThePathStyleUrlItReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] bytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];
        await using var content = new MemoryStream(bytes);

        var result = await Sut.UploadAsync(
            "users/42/q1 report.pdf",
            content,
            "application/pdf",
            FileUploadOptions.Attachment("Q1 report.pdf", "private, max-age=60"),
            ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new Uri(localStack.ServiceUrl, $"{_bucket}/users/42/q1%20report.pdf"));

        var metadata = await S3.GetObjectMetadataAsync(_bucket, "users/42/q1 report.pdf", ct);
        metadata.Headers.ContentType.Should().Be("application/pdf");
        metadata.Headers.ContentDisposition.Should().Be(FileUploadOptions.Attachment("Q1 report.pdf").ContentDisposition);
        // Compared as a directive set: .NET re-serializes a parsed Cache-Control header in its own order.
        metadata.Headers.CacheControl.Split(',', StringSplitOptions.TrimEntries)
            .Should().BeEquivalentTo("private", "max-age=60");
        metadata.ContentLength.Should().Be(bytes.Length);

        // The returned URL is the object's real address: LocalStack serves it without signing.
        using var http = new HttpClient();
        (await http.GetByteArrayAsync(result.Value, ct)).Should().Equal(bytes);
        content.CanRead.Should().BeTrue("the caller owns the stream");
    }

    [Fact]
    public async Task Delete_RemovesTheObject_AndADeleteOfAMissingKeySucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var content = new MemoryStream([1, 2, 3]);
        (await Sut.UploadAsync("avatars/7.png", content, "image/png", ct)).IsSuccess.Should().BeTrue();

        (await Sut.DeleteAsync("avatars/7.png", ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteAsync("avatars/7.png", ct)).IsSuccess.Should().BeTrue("delete-if-exists semantics, as with the Blob store");

        var listing = await S3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket }, ct);
        (listing.S3Objects ?? []).Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_ToAMissingBucket_IsTheUploadFailedResult()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddS3FileStorage(LocalStackFixture.ConfigurationWith(localStack.FileStorageSettings($"missing-{Guid.NewGuid():N}")));
        await using var provider = services.BuildServiceProvider();
        await using var content = new MemoryStream([1]);

        var result = await provider.GetRequiredService<IFileStorageService>()
            .UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.UploadFailed");
    }
}
