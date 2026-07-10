using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class CachingQueryDecoratorTests
{
    // ── Non-cacheable query passes through ──
    [Fact]
    public async Task HandleAsync_NonCacheableQuery_PassesThroughToInnerHandler()
    {
        var inner = new Mock<IQueryHandler<NonCacheableTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<NonCacheableTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));

        var sut = new CachingQueryDecorator<NonCacheableTestQuery, Result<string>>(inner.Object, cacheService.Object);

        Result<string> result = await sut.HandleAsync(new NonCacheableTestQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("data");
        cacheService.Verify(x => x.GetAsync<Result<string>>(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cacheable query with cache hit ──
    [Fact]
    public async Task HandleAsync_CacheableQueryWithCacheHit_ReturnsCachedResult()
    {
        var inner = new Mock<IQueryHandler<CacheableTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        var cachedResult = Result.Success("cached-data");
        cacheService.Setup(x => x.GetAsync<Result<string>>("test-cache-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedResult);

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(inner.Object, cacheService.Object);

        Result<string> result = await sut.HandleAsync(new CacheableTestQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("cached-data");
        inner.Verify(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cacheable query with cache miss ──
    [Fact]
    public async Task HandleAsync_CacheableQueryWithCacheMiss_ExecutesHandlerAndCachesResult()
    {
        var inner = new Mock<IQueryHandler<CacheableTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("test-cache-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("fresh-data"));

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(inner.Object, cacheService.Object);

        Result<string> result = await sut.HandleAsync(new CacheableTestQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("fresh-data");
        inner.Verify(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        cacheService.Verify(
            x => x.SetAsync("test-cache-key", It.IsAny<Result<string>>(), TimeSpan.FromMinutes(5), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Failed result not cached ──
    [Fact]
    public async Task HandleAsync_CacheableQueryWithFailedResult_DoesNotCacheResult()
    {
        var inner = new Mock<IQueryHandler<CacheableTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("test-cache-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<string>(Error.Failure("test", "failed")));

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(inner.Object, cacheService.Object);

        Result<string> result = await sut.HandleAsync(new CacheableTestQuery());

        result.IsFailure.Should().BeTrue();
        cacheService.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Stampede protection: concurrent misses on one key execute the handler once ──
    [Fact]
    public async Task HandleAsync_ConcurrentMissesOnSameKey_ExecuteHandlerOnce()
    {
        var inner = new Mock<IQueryHandler<StampedeTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        Result<string>? stored = null;
        var executions = 0;

        cacheService.Setup(x => x.GetAsync<Result<string>>("stampede-cache-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);
        cacheService.Setup(x => x.SetAsync(
                "stampede-cache-key", It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, Result<string> value, TimeSpan? _, CancellationToken _) => stored = value)
            .Returns(Task.CompletedTask);
        inner.Setup(x => x.HandleAsync(It.IsAny<StampedeTestQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref executions);
                await Task.Delay(50);
                return Result.Success("expensive-data");
            });

        var sut = new CachingQueryDecorator<StampedeTestQuery, Result<string>>(inner.Object, cacheService.Object);

        Result<string>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => sut.HandleAsync(new StampedeTestQuery()))));

        executions.Should().Be(1, "concurrent misses on the same key must collapse to a single handler execution");
        results.Should().AllSatisfy(r =>
        {
            r.IsSuccess.Should().BeTrue();
            r.Value.Should().Be("expensive-data");
        });
    }
}

// ── Test helpers ──
public sealed record NonCacheableTestQuery;

public sealed record CacheableTestQuery : IQueryCacheable
{
    public string CacheKey => "test-cache-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record StampedeTestQuery : IQueryCacheable
{
    public string CacheKey => "stampede-cache-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}
