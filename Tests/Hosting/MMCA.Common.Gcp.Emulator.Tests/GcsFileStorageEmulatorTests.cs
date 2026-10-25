using AwesomeAssertions;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;

namespace MMCA.Common.Gcp.Emulator.Tests;

/// <summary>
/// The shipped Cloud Storage store, registered through <c>AddGoogleCloudStorage</c> exactly as a host
/// would, against fake-gcs-server's JSON API: the resumable upload the client sends is accepted, the
/// headers come back as object metadata, the returned URL addresses the object, a delete is
/// idempotent, and a missing bucket is the upload-failed result rather than an exception.
/// </summary>
public sealed class GcsFileStorageEmulatorTests(FakeGcsServerFixture emulator) : IAsyncLifetime
{
    private readonly string _bucket = $"mmca-{Guid.NewGuid():N}";
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddGoogleCloudStorage(FakeGcsServerFixture.ConfigurationWith(emulator.FileStorageSettings(_bucket)));
        _provider = services.BuildServiceProvider();

        // The adapter never creates its bucket (infrastructure provisions it), so the test does.
        await Client.CreateBucketAsync("mmca-test", _bucket, cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private StorageClient Client => _provider.GetRequiredService<StorageClient>();

    private IFileStorageService Sut => _provider.GetRequiredService<IFileStorageService>();

    [Fact]
    public async Task Upload_StoresTheBytesAndHeaders_AtTheUrlItReturns()
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
        result.Value.Should().Be(new Uri(emulator.ServiceUrl, $"{_bucket}/users/42/q1%20report.pdf"));

        var stored = await Client.GetObjectAsync(_bucket, "users/42/q1 report.pdf", cancellationToken: ct);
        stored.ContentType.Should().Be("application/pdf");
        stored.ContentDisposition.Should().Be(FileUploadOptions.Attachment("Q1 report.pdf").ContentDisposition);
        stored.CacheControl.Should().Be("private, max-age=60");
        stored.Size.Should().Be((ulong)bytes.Length);

        // The returned URL is the object's real address: the emulator serves it without signing.
        using var http = new HttpClient();
        (await http.GetByteArrayAsync(result.Value, ct)).Should().Equal(bytes);
        content.CanRead.Should().BeTrue("the caller owns the stream");
    }

    [Fact]
    public async Task Delete_RemovesTheObject_AndADeleteOfAMissingObjectSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var content = new MemoryStream([1, 2, 3]);
        (await Sut.UploadAsync("avatars/7.png", content, "image/png", ct)).IsSuccess.Should().BeTrue();

        (await Sut.DeleteAsync("avatars/7.png", ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteAsync("avatars/7.png", ct)).IsSuccess.Should().BeTrue("delete-if-exists semantics, as with the Blob and S3 stores");

        var remaining = new List<string>();
        await foreach (var stored in Client.ListObjectsAsync(_bucket).WithCancellation(ct))
        {
            remaining.Add(stored.Name);
        }

        remaining.Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_ToAMissingBucket_IsTheUploadFailedResult()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddGoogleCloudStorage(FakeGcsServerFixture.ConfigurationWith(emulator.FileStorageSettings($"missing-{Guid.NewGuid():N}")));
        await using var provider = services.BuildServiceProvider();
        await using var content = new MemoryStream([1]);

        var result = await provider.GetRequiredService<IFileStorageService>()
            .UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.UploadFailed");
    }
}
