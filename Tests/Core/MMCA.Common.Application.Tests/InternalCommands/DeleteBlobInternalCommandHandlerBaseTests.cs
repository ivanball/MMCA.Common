using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.InternalCommands;

/// <summary>
/// Tests for <see cref="DeleteBlobInternalCommandHandlerBase{TCommand}"/>: the three outcomes of an
/// at-least-once blob delete. Success passes through, an already-missing blob completes the row, and
/// any other storage failure is returned unchanged so the processor retries it.
/// </summary>
public sealed class DeleteBlobInternalCommandHandlerBaseTests
{
    private const string Blob = "avatars/42.png";

    private readonly Mock<IFileStorageService> _storage = new();

    [Fact]
    public async Task HandleAsync_WhenTheDeleteSucceeds_ReturnsSuccess()
    {
        _storage.Setup(s => s.DeleteAsync(Blob, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());

        var result = await CreateHandler().HandleAsync(new TestDeleteBlobCommand(Blob), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _storage.Verify(s => s.DeleteAsync(Blob, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenTheBlobIsAlreadyGone_ReturnsSuccess()
    {
        _storage.Setup(s => s.DeleteAsync(Blob, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFoundError("Storage.BlobNotFound", "No such blob.")));

        var result = await CreateHandler().HandleAsync(new TestDeleteBlobCommand(Blob), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenStorageFailsOtherwise_ReturnsTheFailureUnchanged()
    {
        var failure = Result.Failure(Error.Failure("Storage.Unavailable", "The store is down."));
        _storage.Setup(s => s.DeleteAsync(Blob, It.IsAny<CancellationToken>())).ReturnsAsync(failure);

        var result = await CreateHandler().HandleAsync(new TestDeleteBlobCommand(Blob), TestContext.Current.CancellationToken);

        result.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task HandleAsync_WithANullCommand_Throws()
    {
        var act = () => CreateHandler().HandleAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private TestDeleteBlobHandler CreateHandler() => new(_storage.Object);

    private sealed record TestDeleteBlobCommand(string BlobName) : IDeleteBlobInternalCommand;

    private sealed class TestDeleteBlobHandler(IFileStorageService fileStorage)
        : DeleteBlobInternalCommandHandlerBase<TestDeleteBlobCommand>(fileStorage, NullLogger.Instance)
    {
        protected override string BlobKind => "Test blob";
    }
}
