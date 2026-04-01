using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Caching.Distributed;
using MMCA.Common.Infrastructure.Caching;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.Infrastructure.Tests.Caching;

public class DistributedCacheServiceTests
{
    private readonly Mock<IDistributedCache> _cacheMock = new();
    private readonly DistributedCacheService _sut;

    public DistributedCacheServiceTests() =>
        _sut = new DistributedCacheService(_cacheMock.Object);

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

        var sut = new DistributedCacheService(cacheMock.Object, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        dbMock.Verify(d => d.KeyDeleteAsync((RedisKey)"prefix:key1", It.IsAny<CommandFlags>()), Times.Once);
        dbMock.Verify(d => d.KeyDeleteAsync((RedisKey)"prefix:key2", It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task RemoveByPrefixAsync_WithRedis_NoServers_IsNoOp()
    {
        var cacheMock = new Mock<IDistributedCache>();
        var connectionMock = new Mock<IConnectionMultiplexer>();
        connectionMock.Setup(c => c.GetServers()).Returns([]);

        var sut = new DistributedCacheService(cacheMock.Object, connectionMock.Object);

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

        var sut = new DistributedCacheService(cacheMock.Object, connectionMock.Object);

        await sut.RemoveByPrefixAsync("prefix:");

        dbMock.Verify(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
    }
}
