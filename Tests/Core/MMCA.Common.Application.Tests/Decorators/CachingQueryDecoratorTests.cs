using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Settings;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class CachingQueryDecoratorTests
{
    // Duplicated literal: CqrsMetrics.MeterName is internal, so the counters are observed exactly
    // the way an OpenTelemetry exporter observes them (by meter name).
    private const string MeterName = "MMCA.Common.Cqrs";

    private sealed record CapturedCounter(string InstrumentName, long Value, Dictionary<string, object?> Tags);

    // ── Counter capture helper ──
    // The meter is a process-wide static shared with tests running in parallel, so measurements are
    // filtered down to this file's probe query types by the "query" tag.
    private static async Task<List<CapturedCounter>> CaptureCountersAsync(Func<Task> act, string queryTagValue)
    {
        var gate = new System.Threading.Lock();
        var captured = new List<CapturedCounter>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagDictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                tagDictionary[tag.Key] = tag.Value;
            }

            lock (gate)
            {
                captured.Add(new CapturedCounter(instrument.Name, value, tagDictionary));
            }
        });
        listener.Start();

        await act();

        lock (gate)
        {
            return [.. captured.Where(m => m.Tags.TryGetValue("query", out var v) && Equals(v, queryTagValue))];
        }
    }

    // ── Non-cacheable query passes through ──
    [Fact]
    public async Task HandleAsync_NonCacheableQuery_PassesThroughToInnerHandler()
    {
        var inner = new Mock<IQueryHandler<NonCacheableTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        inner.Setup(x => x.HandleAsync(It.IsAny<NonCacheableTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("data"));

        var sut = new CachingQueryDecorator<NonCacheableTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<NonCacheableTestQuery, Result<string>>>.Instance);

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

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheableTestQuery, Result<string>>>.Instance);

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

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheableTestQuery, Result<string>>>.Instance);

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

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheableTestQuery, Result<string>>>.Instance);

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

        var sut = new CachingQueryDecorator<StampedeTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<StampedeTestQuery, Result<string>>>.Instance);

        Result<string>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => sut.HandleAsync(new StampedeTestQuery()))));

        executions.Should().Be(1, "concurrent misses on the same key must collapse to a single handler execution");
        results.Should().AllSatisfy(r =>
        {
            r.IsSuccess.Should().BeTrue();
            r.Value.Should().Be("expensive-data");
        });
    }

    // ── Populate-lock budget: a waiter that cannot take the stripe runs uncached ──
    [Fact]
    public async Task HandleAsync_WhenThePopulateLockIsHeldPastTheBudget_ExecutesTheHandlerUncached()
    {
        var inner = new Mock<IQueryHandler<PopulateLockTimeoutQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("populate-lock-timeout-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);
        inner.Setup(x => x.HandleAsync(It.IsAny<PopulateLockTimeoutQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("uncached-data"));

        var sut = new CachingQueryDecorator<PopulateLockTimeoutQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<PopulateLockTimeoutQuery, Result<string>>>.Instance,
            tenantContext: null,
            pipelineSettings: Options.Create(new QueryCachePipelineSettings
            {
                PopulateLockTimeout = TimeSpan.FromMilliseconds(50),
            }));

        Result<string> result;

        // Hold the stripe this key maps to for the whole call, so the decorator can only reach the
        // handler by giving up on the lock.
        using (await QueryCacheKeyLocks.Locks.AcquireAsync("populate-lock-timeout-key"))
        {
            result = await sut.HandleAsync(new PopulateLockTimeoutQuery());
        }

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("uncached-data");
        inner.Verify(
            x => x.HandleAsync(It.IsAny<PopulateLockTimeoutQuery>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "fail-open: an expired budget degrades the query to an uncached read, it never fails it");
        cacheService.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the request holding the lock is the one that populates the entry");
        cacheService.Verify(
            x => x.GetAsync<Result<string>>("populate-lock-timeout-key", It.IsAny<CancellationToken>()),
            Times.Once,
            "only the fast-path read runs; the double-check inside the lock is never reached");
    }

    // ── A cache outage on the populate must not fail an already-successful read (M28) ──
    [Fact]
    public async Task HandleAsync_WhenCachePopulateThrows_StillReturnsHandlerResult()
    {
        var inner = new Mock<IQueryHandler<CacheableTestQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("test-cache-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);
        cacheService.Setup(x => x.SetAsync(
                It.IsAny<string>(), It.IsAny<Result<string>>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unreachable"));
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("fresh-data"));

        var sut = new CachingQueryDecorator<CacheableTestQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheableTestQuery, Result<string>>>.Instance);

        Result<string> result = await sut.HandleAsync(new CacheableTestQuery());

        // The read succeeded; the failed populate is swallowed and the handler's result flows out.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("fresh-data");
        inner.Verify(x => x.HandleAsync(It.IsAny<CacheableTestQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── A cache outage on the READ paths degrades to an uncached query, not a 500 ──
    [Fact]
    public async Task HandleAsync_WhenCacheReadThrows_ExecutesInnerHandlerAndReturnsResult()
    {
        var inner = new Mock<IQueryHandler<CacheReadFailureQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();

        // Throws on BOTH reads: the fast path and the double-check inside the per-key lock.
        cacheService.Setup(x => x.GetAsync<Result<string>>("cache-read-failure-key", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unreachable"));
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheReadFailureQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("fresh-data"));

        var sut = new CachingQueryDecorator<CacheReadFailureQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheReadFailureQuery, Result<string>>>.Instance);

        Result<string> result = await sut.HandleAsync(new CacheReadFailureQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("fresh-data");
        inner.Verify(x => x.HandleAsync(It.IsAny<CacheReadFailureQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        cacheService.Verify(
            x => x.GetAsync<Result<string>>("cache-read-failure-key", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Genuine cancellation from the cache still propagates ──
    [Fact]
    public async Task HandleAsync_WhenCacheReadThrowsOperationCanceled_Propagates()
    {
        var inner = new Mock<IQueryHandler<CacheReadCanceledQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("cache-read-canceled-key", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sut = new CachingQueryDecorator<CacheReadCanceledQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheReadCanceledQuery, Result<string>>>.Instance);

        var invoke = () => sut.HandleAsync(new CacheReadCanceledQuery());

        await invoke.Should().ThrowAsync<OperationCanceledException>();
        inner.Verify(x => x.HandleAsync(It.IsAny<CacheReadCanceledQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Miss counter: exactly one per executed query ──
    [Fact]
    public async Task HandleAsync_OnCacheMiss_RecordsExactlyOneMissAndNoHit()
    {
        var inner = new Mock<IQueryHandler<CacheMissMetricQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("cache-miss-metric-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null);
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheMissMetricQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("fresh-data"));

        var sut = new CachingQueryDecorator<CacheMissMetricQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheMissMetricQuery, Result<string>>>.Instance);

        var measurements = await CaptureCountersAsync(
            () => sut.HandleAsync(new CacheMissMetricQuery()),
            nameof(CacheMissMetricQuery));

        // Two reads (fast path + double-check) both missed, but only ONE query executed, so the
        // counter must read 1, not 2.
        var measurement = measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be("cqrs.query.cache.miss");
        measurement.Value.Should().Be(1);
    }

    // ── Miss counter: a FAILED read counts as one miss, not two ──
    [Fact]
    public async Task HandleAsync_WhenCacheReadThrows_RecordsExactlyOneMiss()
    {
        var inner = new Mock<IQueryHandler<CacheReadFailureMetricQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>(
                "cache-read-failure-metric-key", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unreachable"));
        inner.Setup(x => x.HandleAsync(It.IsAny<CacheReadFailureMetricQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("fresh-data"));

        var sut = new CachingQueryDecorator<CacheReadFailureMetricQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheReadFailureMetricQuery, Result<string>>>.Instance);

        var measurements = await CaptureCountersAsync(
            () => sut.HandleAsync(new CacheReadFailureMetricQuery()),
            nameof(CacheReadFailureMetricQuery));

        var measurement = measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be("cqrs.query.cache.miss");
        measurement.Value.Should().Be(1);
    }

    // ── Hit counter: fast-path hit ──
    [Fact]
    public async Task HandleAsync_OnFastPathCacheHit_RecordsExactlyOneHitAndNoMiss()
    {
        var inner = new Mock<IQueryHandler<CacheHitMetricQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(x => x.GetAsync<Result<string>>("cache-hit-metric-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success("cached-data"));

        var sut = new CachingQueryDecorator<CacheHitMetricQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheHitMetricQuery, Result<string>>>.Instance);

        var measurements = await CaptureCountersAsync(
            () => sut.HandleAsync(new CacheHitMetricQuery()),
            nameof(CacheHitMetricQuery));

        var measurement = measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be("cqrs.query.cache.hit");
        measurement.Value.Should().Be(1);
        inner.Verify(x => x.HandleAsync(It.IsAny<CacheHitMetricQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Hit counter: the double-check hit inside the lock counts too ──
    [Fact]
    public async Task HandleAsync_OnDoubleCheckCacheHit_RecordsExactlyOneHitAndNoMiss()
    {
        var inner = new Mock<IQueryHandler<CacheDoubleCheckMetricQuery, Result<string>>>();
        var cacheService = new Mock<ICacheService>();

        // Fast path misses, the double-check inside the lock is served by a concurrent populate.
        cacheService.SetupSequence(x => x.GetAsync<Result<string>>(
                "cache-double-check-metric-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Result<string>?)null)
            .ReturnsAsync(Result.Success("cached-data"));

        var sut = new CachingQueryDecorator<CacheDoubleCheckMetricQuery, Result<string>>(
            inner.Object,
            cacheService.Object,
            NullLogger<CachingQueryDecorator<CacheDoubleCheckMetricQuery, Result<string>>>.Instance);

        var measurements = await CaptureCountersAsync(
            () => sut.HandleAsync(new CacheDoubleCheckMetricQuery()),
            nameof(CacheDoubleCheckMetricQuery));

        var measurement = measurements.Should().ContainSingle().Which;
        measurement.InstrumentName.Should().Be("cqrs.query.cache.hit");
        measurement.Value.Should().Be(1);
        inner.Verify(
            x => x.HandleAsync(It.IsAny<CacheDoubleCheckMetricQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

// ── Fail-open + counter probes (one type per test so the "query" tag isolates the measurements
// from parallel tests sharing the process-wide static meter, and one cache key per type so the
// process-wide per-key lock table cannot couple them) ──
public sealed record CacheReadFailureQuery : IQueryCacheable
{
    public string CacheKey => "cache-read-failure-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record CacheReadCanceledQuery : IQueryCacheable
{
    public string CacheKey => "cache-read-canceled-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record PopulateLockTimeoutQuery : IQueryCacheable
{
    public string CacheKey => "populate-lock-timeout-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record CacheMissMetricQuery : IQueryCacheable
{
    public string CacheKey => "cache-miss-metric-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record CacheReadFailureMetricQuery : IQueryCacheable
{
    public string CacheKey => "cache-read-failure-metric-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record CacheHitMetricQuery : IQueryCacheable
{
    public string CacheKey => "cache-hit-metric-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record CacheDoubleCheckMetricQuery : IQueryCacheable
{
    public string CacheKey => "cache-double-check-metric-key";
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}
