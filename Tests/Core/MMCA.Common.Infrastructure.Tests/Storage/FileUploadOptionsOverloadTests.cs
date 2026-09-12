using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Storage;

/// <summary>
/// Tests for the options-carrying <c>UploadAsync</c> overload on <see cref="IFileStorageService"/>
/// (ADR-045). It is a default interface member on purpose: an implementation written against the
/// original three-argument contract keeps compiling and still receives the call, while implementations
/// that care about headers override it. <see cref="NullFileStorageService"/> answers the unconfigured
/// failure through either overload.
/// </summary>
public sealed class FileUploadOptionsOverloadTests
{
    /// <summary>
    /// A store that implements ONLY the original three-argument contract, which is what proves the new
    /// member is non-breaking: this class would not compile if the overload were abstract.
    /// </summary>
    private sealed class LegacyFileStorageService : IFileStorageService
    {
        public bool IsConfigured => true;

        public string? LastContentType { get; private set; }

        public int UploadCount { get; private set; }

        public Task<Result<Uri>> UploadAsync(string blobName, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            UploadCount++;
            LastContentType = contentType;
            return Task.FromResult(Result.Success(new Uri("https://example.invalid/" + blobName)));
        }

        public Task<Result> DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    [Fact]
    public async Task LegacyImplementation_ReceivesTheCallThroughTheDefaultInterfaceMember()
    {
        var store = new LegacyFileStorageService();
        IFileStorageService sut = store;
        await using var content = new MemoryStream([0x25, 0x50, 0x44, 0x46, 0x2D]);

        Result<Uri> result = await sut.UploadAsync(
            "documents/deck.pptx",
            content,
            "application/pdf",
            FileUploadOptions.Attachment("deck.pptx"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new Uri("https://example.invalid/documents/deck.pptx"));
        store.UploadCount.Should().Be(1, "the default member forwards to the three-argument overload");
        store.LastContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task NullFileStorageService_WithOptions_FailsWithTheSameUnconfiguredError()
    {
        var sut = new NullFileStorageService();
        await using var content = new MemoryStream([0x25, 0x50, 0x44, 0x46, 0x2D]);

        Result<Uri> withOptions = await sut.UploadAsync(
            "documents/deck.pptx",
            content,
            "application/pdf",
            FileUploadOptions.Attachment("deck.pptx"),
            TestContext.Current.CancellationToken);
        Result<Uri> withoutOptions = await sut.UploadAsync(
            "documents/deck.pptx",
            content,
            "application/pdf",
            TestContext.Current.CancellationToken);

        withOptions.IsFailure.Should().BeTrue();
        withOptions.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.NotConfigured");
        withoutOptions.Errors.Should().ContainSingle().Which.Code.Should().Be("FileStorage.NotConfigured");
        sut.IsConfigured.Should().BeFalse();
    }
}
