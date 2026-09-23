using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Infrastructure.Storage;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Tests.Storage;

/// <summary>
/// Tests for the options-carrying <c>UploadAsync</c> overload on <see cref="IFileStorageService"/>
/// (ADR-045), which every implementation provides: <see cref="NullFileStorageService"/> answers the
/// unconfigured failure through either overload.
/// </summary>
public sealed class FileUploadOptionsOverloadTests
{
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
