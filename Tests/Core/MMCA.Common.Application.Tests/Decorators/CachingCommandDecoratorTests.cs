using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class CachingCommandDecoratorTests
{
    // ── Success with cache-invalidating command invalidates cache ──
    [Fact]
    public async Task HandleAsync_SuccessfulCacheInvalidatingCommand_RemovesCacheByPrefix()
    {
        var inner = new Mock<ICommandHandler<CacheInvalidatingTestCommand, Result>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheInvalidatingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new CachingCommandDecorator<CacheInvalidatingTestCommand, Result>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingCommandDecorator<CacheInvalidatingTestCommand, Result>>.Instance);

        var result = await sut.HandleAsync(new CacheInvalidatingTestCommand());

        result.IsSuccess.Should().BeTrue();
        cacheService.Verify(
            x => x.RemoveByPrefixAsync("test-prefix", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Failure with cache-invalidating command does NOT invalidate cache ──
    [Fact]
    public async Task HandleAsync_FailedCacheInvalidatingCommand_DoesNotInvalidateCache()
    {
        var inner = new Mock<ICommandHandler<CacheInvalidatingTestCommand, Result>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheInvalidatingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("test", "failed")));

        var sut = new CachingCommandDecorator<CacheInvalidatingTestCommand, Result>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingCommandDecorator<CacheInvalidatingTestCommand, Result>>.Instance);

        var result = await sut.HandleAsync(new CacheInvalidatingTestCommand());

        result.IsFailure.Should().BeTrue();
        cacheService.Verify(
            x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Non-cache-invalidating command does not touch cache ──
    [Fact]
    public async Task HandleAsync_NonCacheInvalidatingCommand_DoesNotTouchCache()
    {
        var inner = new Mock<ICommandHandler<PlainTestCommand, Result>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<PlainTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new CachingCommandDecorator<PlainTestCommand, Result>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingCommandDecorator<PlainTestCommand, Result>>.Instance);

        await sut.HandleAsync(new PlainTestCommand());

        cacheService.Verify(
            x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── A cache outage after commit must not fail the command (H15) ──
    [Fact]
    public async Task HandleAsync_WhenCacheInvalidationThrows_StillReturnsSuccessfulResult()
    {
        var inner = new Mock<ICommandHandler<CacheInvalidatingTestCommand, Result>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheInvalidatingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        cacheService.Setup(x => x.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unreachable"));

        var sut = new CachingCommandDecorator<CacheInvalidatingTestCommand, Result>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingCommandDecorator<CacheInvalidatingTestCommand, Result>>.Instance);

        var result = await sut.HandleAsync(new CacheInvalidatingTestCommand());

        // The command committed; a best-effort invalidation failure must not turn it into a failure.
        result.IsSuccess.Should().BeTrue();
        cacheService.Verify(
            x => x.RemoveByPrefixAsync("test-prefix", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Post-commit cleanup is not cancelled by a client disconnect (L1) ──
    [Fact]
    public async Task HandleAsync_SuccessfulCacheInvalidatingCommand_InvalidatesWithNonCancellableToken()
    {
        var inner = new Mock<ICommandHandler<CacheInvalidatingTestCommand, Result>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheInvalidatingTestCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new CachingCommandDecorator<CacheInvalidatingTestCommand, Result>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingCommandDecorator<CacheInvalidatingTestCommand, Result>>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await sut.HandleAsync(new CacheInvalidatingTestCommand(), cts.Token);

        result.IsSuccess.Should().BeTrue();
        cacheService.Verify(
            x => x.RemoveByPrefixAsync("test-prefix", CancellationToken.None),
            Times.Once);
    }
}

// ── Test types (must be public for Moq DynamicProxy) ──
public sealed record PlainTestCommand;

public sealed record CacheInvalidatingTestCommand : ICacheInvalidating
{
    public string CachePrefix => "test-prefix";
}
