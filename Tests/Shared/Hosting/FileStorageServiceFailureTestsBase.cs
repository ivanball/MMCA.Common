using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;

namespace MMCA.Common.Tests.Shared.Hosting;

/// <summary>
/// The <see cref="IFileStorageService"/> failure contract the S3 and Cloud Storage stores share: a
/// service or transport failure on upload is a <c>FileStorage.UploadFailed</c> Result, a service failure
/// on delete is a <c>FileStorage.DeleteFailed</c> Result, and null options are refused by the upload
/// overload and by the constructor. Each subclass supplies the store over its mocked client and makes
/// that client fail; the request shape, headers, URLs and vendor-only failure modes stay in the subclass.
/// </summary>
/// <remarks>Linked as source into MMCA.Common.Aws.Tests and MMCA.Common.Gcp.Tests.</remarks>
public abstract class FileStorageServiceFailureTestsBase
{
    /// <summary>The store under test over the subclass's mocked client and default settings.</summary>
    protected abstract IFileStorageService CreateSut();

    /// <summary>Constructs the store with null options.</summary>
    protected abstract void ConstructWithNullOptions();

    /// <summary>The exception the vendor's client throws for a refused request (access denied).</summary>
    protected abstract Exception CreateServiceException();

    /// <summary>Makes the mocked client's upload call throw <paramref name="exception"/>.</summary>
    protected abstract void FailUploadWith(Exception exception);

    /// <summary>Makes the mocked client's delete call throw <paramref name="exception"/>.</summary>
    protected abstract void FailDeleteWith(Exception exception);

    [Fact]
    public async Task UploadAsync_ServiceFailure_IsTheUploadFailedResult()
    {
        FailUploadWith(CreateServiceException());
        await using var content = new MemoryStream([1]);

        var result = await CreateSut().UploadAsync("a.png", content, "image/png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.UploadFailed");
    }

    // M261: a failed connection is not the vendor's service exception, and it is still a Result failure.
    [Fact]
    public async Task UploadAsync_TransportFailure_IsTheUploadFailedResult()
    {
        FailUploadWith(new HttpRequestException("connection refused"));
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
    public async Task DeleteAsync_ServiceFailure_IsTheDeleteFailedResult()
    {
        FailDeleteWith(CreateServiceException());

        var result = await CreateSut().DeleteAsync("a.png", TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.DeleteFailed");
    }

    [Fact]
    public void Constructor_RejectsNullOptions()
    {
        var act = () => ConstructWithNullOptions();

        act.Should().Throw<ArgumentNullException>();
    }
}
