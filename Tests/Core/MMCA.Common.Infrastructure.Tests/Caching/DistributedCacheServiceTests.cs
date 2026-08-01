using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Caching;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Tests.Caching;

public class DistributedCacheServiceTests
{
    private static readonly ILogger<DistributedCacheService> NullLog = NullLogger<DistributedCacheService>.Instance;

    private readonly Mock<IDistributedCache> _cacheMock = new();
    private readonly DistributedCacheService _sut;

    public DistributedCacheServiceTests() =>
        _sut = new DistributedCacheService(_cacheMock.Object, NullLog);

    // ── GetAsync ──
    [Fact]
    public async Task GetAsync_WhenKeyDoesNotExist_ReturnsDefault()
    {
        _cacheMock.Setup(c => c.GetAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var result = await _sut.GetAsync<string>("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenKeyExists_ReturnsDeserializedValue()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes("hello");
        _cacheMock.Setup(c => c.GetAsync("key1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);

        var result = await _sut.GetAsync<string>("key1");
        result.Should().Be("hello");
    }

    [Fact]
    public async Task GetAsync_WithIntValue_ReturnsDeserializedInt()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(42);
        _cacheMock.Setup(c => c.GetAsync("int-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);

        var result = await _sut.GetAsync<int>("int-key");
        result.Should().Be(42);
    }

    // ── SetAsync ──
    [Fact]
    public async Task SetAsync_CallsDistributedCacheSetAsync()
    {
        await _sut.SetAsync("key", "value");

        _cacheMock.Verify(c => c.SetAsync(
            "key",
            It.IsAny<byte[]>(),
            It.IsAny<DistributedCacheEntryOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAsync_WithExpiration_PassesOptions()
    {
        var expiration = TimeSpan.FromMinutes(10);
        await _sut.SetAsync("key", "value", expiration);

        _cacheMock.Verify(c => c.SetAsync(
            "key",
            It.IsAny<byte[]>(),
            It.Is<DistributedCacheEntryOptions>(o =>
                o.AbsoluteExpirationRelativeToNow == expiration),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── RemoveAsync ──
    [Fact]
    public async Task RemoveAsync_CallsDistributedCacheRemoveAsync()
    {
        await _sut.RemoveAsync("key");

        _cacheMock.Verify(
            c => c.RemoveAsync("key", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── RemoveByPrefixAsync ──
    [Fact]
    public async Task RemoveByPrefixAsync_WithoutRedis_IsNoOp()
    {
        await _sut.RemoveByPrefixAsync("prefix:");

        _cacheMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithoutRedis_LogsWarningOnceAcrossCalls()
    {
        // Moq cannot proxy ILogger<T> of an internal T (strong-named abstractions), so use a small fake.
        var logger = new WarningCountingLogger();
        var sut = new DistributedCacheService(_cacheMock.Object, logger);

        // The dead no-op should be observable, but warn once (steady state) rather than on every command.
        await sut.RemoveByPrefixAsync("prefix:");
        await sut.RemoveByPrefixAsync("other:");

        logger.WarningCount.Should().Be(1);
    }

    private sealed class WarningCountingLogger : ILogger<DistributedCacheService>
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningCount++;
        }
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_DeletesMatchingKeys()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var serverMock = new Mock<IServer>();
        var dbMock = new Mock<IDatabase>();

        connectionMock.Setup(c => c.GetServers()).Returns([serverMock.Object]);
        connectionMock.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        RedisKey[] keys = [(RedisKey)"prefix:key1", (RedisKey)"prefix:key2"];
        serverMock.Setup(s => s.KeysAsync(
                It.IsAny<int>(),
                It.Is<RedisValue>(v => v == "prefix:*"),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(keys.ToAsyncEnumerable());

        dbMock.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var sut = new DistributedCacheService(cacheMock.Object, NullLog, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        // One command per key, never a multi-key DEL. The keys behind a prefix hash to arbitrary
        // slots, and under cluster policy StackExchange.Redis answers a cross-slot multi-key command
        // by throwing rather than under-deleting, so batching them would fault the invalidation.
        dbMock.Verify(
            d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k == (RedisKey)"prefix:key1"), It.IsAny<CommandFlags>()),
            Times.Once);
        dbMock.Verify(
            d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k == (RedisKey)"prefix:key2"), It.IsAny<CommandFlags>()),
            Times.Once);
        dbMock.Verify(d => d.KeyDeleteAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_NoServers_IsNoOp()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var connectionMock = new Mock<IConnectionMultiplexer>();
        connectionMock.Setup(c => c.GetServers()).Returns([]);

        var sut = new DistributedCacheService(cacheMock.Object, NullLog, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        connectionMock.Verify(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_NoMatchingKeys_DoesNotDeleteAnything()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var serverMock = new Mock<IServer>();
        var dbMock = new Mock<IDatabase>();

        connectionMock.Setup(c => c.GetServers()).Returns([serverMock.Object]);
        connectionMock.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);

        serverMock.Setup(s => s.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(AsyncEnumerable.Empty<RedisKey>());

        var sut = new DistributedCacheService(cacheMock.Object, NullLog, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        dbMock.Verify(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    // ── Prefix eviction must cover every primary, not one arbitrary server ──
    // GetServers() reports every endpoint the multiplexer knows. Scanning only the first one leaves
    // the keys held by the other primaries alive until their TTL expires, so an invalidation that
    // reports success has silently done part of the job.
    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_ScansEveryNonReplicaServer()
    {
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var primaryA = ServerStub("primary-a", isReplica: false, "prefix:a1");
        var primaryB = ServerStub("primary-b", isReplica: false, "prefix:b1");

        connectionMock.Setup(c => c.GetServers()).Returns([primaryA.Object, primaryB.Object]);
        connectionMock.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);
        dbMock.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);

        var sut = new DistributedCacheService(new Mock<IDistributedCache>().Object, NullLog, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        VerifyScanned(primaryA, Times.Once());
        VerifyScanned(primaryB, Times.Once());
        VerifyDeleted(dbMock, "prefix:a1", Times.Once());
        VerifyDeleted(dbMock, "prefix:b1", Times.Once());
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_DoesNotScanReplicas()
    {
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var primary = ServerStub("primary", isReplica: false, "prefix:a1");
        var replica = ServerStub("replica", isReplica: true, "prefix:a1");

        connectionMock.Setup(c => c.GetServers()).Returns([primary.Object, replica.Object]);
        connectionMock.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);
        dbMock.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);

        var sut = new DistributedCacheService(new Mock<IDistributedCache>().Object, NullLog, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        // A replica mirrors a primary that was already scanned, and a DEL against it is rejected,
        // so scanning it would only re-issue deletes that cannot succeed.
        VerifyScanned(primary, Times.Once());
        VerifyScanned(replica, Times.Never());
        VerifyDeleted(dbMock, "prefix:a1", Times.Once());
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_AllServersAreReplicas_IsNoOp()
    {
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var replica = ServerStub("replica", isReplica: true, "prefix:a1");

        connectionMock.Setup(c => c.GetServers()).Returns([replica.Object]);

        var sut = new DistributedCacheService(new Mock<IDistributedCache>().Object, NullLog, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        VerifyScanned(replica, Times.Never());
        connectionMock.Verify(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_WhenOneServerScanFails_StillEvictsOnTheOthers()
    {
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var healthy = ServerStub("healthy", isReplica: false, "prefix:ok1");
        var broken = ServerStub("broken", isReplica: false);
        broken.Setup(s => s.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "node unreachable"));

        // Broken first, so a fault that aborted the loop would take the healthy server with it.
        connectionMock.Setup(c => c.GetServers()).Returns([broken.Object, healthy.Object]);
        connectionMock.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);
        dbMock.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);

        var logger = new WarningCountingLogger();
        var sut = new DistributedCacheService(new Mock<IDistributedCache>().Object, logger, connectionMock.Object);

        await FluentActions.Invoking(() => sut.RemoveByPrefixAsync("prefix:"))
            .Should().NotThrowAsync("one unreachable node must not fail the whole invalidation");

        VerifyScanned(healthy, Times.Once());
        VerifyDeleted(dbMock, "prefix:ok1", Times.Once());
        logger.WarningCount.Should().Be(1, "the skipped node must be observable, not silent");
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_WhenOneServerDeleteFails_StillEvictsOnTheOthers()
    {
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var dbMock = new Mock<IDatabase>();
        var failing = ServerStub("failing", isReplica: false, "prefix:bad1");
        var healthy = ServerStub("healthy", isReplica: false, "prefix:ok1");

        connectionMock.Setup(c => c.GetServers()).Returns([failing.Object, healthy.Object]);
        connectionMock.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(dbMock.Object);
        dbMock.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(true);
        dbMock.Setup(d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k == (RedisKey)"prefix:bad1"), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisServerException("CROSSSLOT Keys in request don't hash to the same slot"));

        var logger = new WarningCountingLogger();
        var sut = new DistributedCacheService(new Mock<IDistributedCache>().Object, logger, connectionMock.Object);

        await FluentActions.Invoking(() => sut.RemoveByPrefixAsync("prefix:"))
            .Should().NotThrowAsync();

        VerifyScanned(healthy, Times.Once());
        VerifyDeleted(dbMock, "prefix:ok1", Times.Once());
        logger.WarningCount.Should().Be(1);
    }

    private static Mock<IServer> ServerStub(string host, bool isReplica, params string[] keys)
    {
        var serverMock = new Mock<IServer>();
        serverMock.SetupGet(s => s.IsReplica).Returns(isReplica);
        serverMock.SetupGet(s => s.EndPoint).Returns(new DnsEndPoint(host, 6379));
        serverMock.Setup(s => s.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(keys.Select(k => (RedisKey)k).ToAsyncEnumerable());

        return serverMock;
    }

    private static void VerifyScanned(Mock<IServer> server, Times times) =>
        server.Verify(
            s => s.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()),
            times);

    private static void VerifyDeleted(Mock<IDatabase> db, string key, Times times) =>
        db.Verify(
            d => d.KeyDeleteAsync(It.Is<RedisKey>(k => k == (RedisKey)key), It.IsAny<CommandFlags>()),
            times);

    // ── IncrementAsync must stay inside IDistributedCache's storage format ──
    // StackExchangeRedisCache stores every entry as a Redis HASH (absexp/sldexp/data, read with
    // HMGET). A Redis INCR fast path writes a STRING at the same key, so the next read of that
    // counter fails with WRONGTYPE and surfaces as a 500 on whatever endpoint owns it: registration
    // and login, for the ADR-029 counters. The counter has to be written the same way it is read.
    [Fact]
    public async Task IncrementAsync_PersistsThroughTheDistributedCache_SoItCanBeReadBack()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var sut = new DistributedCacheService(cacheMock.Object, NullLog, connectionMock.Object);

        cacheMock.Setup(c => c.GetAsync("counter", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var value = await sut.IncrementAsync("counter", TimeSpan.FromMinutes(30));

        value.Should().Be(1);
        cacheMock.Verify(
            c => c.SetAsync("counter", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the counter must be written through IDistributedCache, not straight to a Redis string");
        connectionMock.Verify(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Never,
            "bypassing IDistributedCache writes a value its own reader cannot parse");
    }

    [Fact]
    public async Task IncrementAsync_RoundTripsWithGetAsync()
    {
        // The real failure was a write the matching read could not consume, so assert the pair.
        var cacheMock = new Mock<IDistributedCache>();
        var connectionMock = new Mock<IConnectionMultiplexer>();
        var sut = new DistributedCacheService(cacheMock.Object, NullLog, connectionMock.Object);

        byte[]? stored = null;
        cacheMock.Setup(c => c.GetAsync("counter", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => stored);
        cacheMock.Setup(c => c.SetAsync("counter", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, bytes, _, _) => stored = bytes)
            .Returns(Task.CompletedTask);

        await sut.IncrementAsync("counter", TimeSpan.FromMinutes(30));
        await sut.IncrementAsync("counter", TimeSpan.FromMinutes(30));

        (await sut.GetAsync<long?>("counter")).Should().Be(2);
    }

    [Fact]
    public async Task IncrementAsync_AppliesTheKeyNamespaceConsistentlyWithGetAsync()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var sut = new DistributedCacheService(
            cacheMock.Object, NullLog, connectionMultiplexer: null, new CacheKeyNamespace("svc:"));

        cacheMock.Setup(c => c.GetAsync("svc:counter", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        await sut.IncrementAsync("counter", TimeSpan.FromMinutes(30));

        cacheMock.Verify(
            c => c.SetAsync("svc:counter", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
