using AwesomeAssertions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Caching;
using Moq;

namespace MMCA.Common.API.Tests.Caching;

/// <summary>
/// Unit tests for the multi-tag eviction helpers on <see cref="IOutputCacheStore"/>. They replace the
/// per-controller private helpers that wrapped a run of <c>EvictByTagAsync</c> calls, so what matters
/// is that every named tag is evicted, in order, and that the best-effort variant never lets a cache
/// outage surface as a failure of the write it follows.
/// </summary>
public sealed class OutputCacheEvictTagsTests
{
    [Fact]
    public async Task EvictTagsAsync_EvictsEveryTagInOrder()
    {
        var evicted = new List<string>();
        var store = new Mock<IOutputCacheStore>();
        store.Setup(s => s.EvictByTagAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string tag, CancellationToken _) => evicted.Add(tag))
            .Returns(ValueTask.CompletedTask);

        await store.Object.EvictTagsAsync(CancellationToken.None, "conference:sessions", "conference");

        evicted.Should().Equal("conference:sessions", "conference");
    }

    [Fact]
    public async Task EvictTagsAsync_PassesTheCallersToken()
    {
        using var cts = new CancellationTokenSource();
        var store = new Mock<IOutputCacheStore>();

        await store.Object.EvictTagsAsync(cts.Token, "catalog:products");

        store.Verify(s => s.EvictByTagAsync("catalog:products", cts.Token), Times.Once);
    }

    [Fact]
    public async Task EvictTagsAsync_WithNoTags_DoesNotTouchTheStore()
    {
        var store = new Mock<IOutputCacheStore>(MockBehavior.Strict);

        var act = async () => await store.Object.EvictTagsAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EvictTagsAsync_PropagatesAStoreFailure()
    {
        var store = new Mock<IOutputCacheStore>();
        store.Setup(s => s.EvictByTagAsync("conference", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var act = async () => await store.Object.EvictTagsAsync(CancellationToken.None, "conference");

        await act.Should().ThrowAsync<InvalidOperationException>(
            "the plain helper is a straight sequence of evictions; a caller that wants the failure swallowed uses TryEvictTagsAsync");
    }

    [Fact]
    public async Task TryEvictTagsAsync_EvictsEveryTag()
    {
        var evicted = new List<string>();
        var store = new Mock<IOutputCacheStore>();
        store.Setup(s => s.EvictByTagAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string tag, CancellationToken _) => evicted.Add(tag))
            .Returns(ValueTask.CompletedTask);

        await store.Object.TryEvictTagsAsync(NullLogger.Instance, "catalog:products", "catalog");

        evicted.Should().Equal("catalog:products", "catalog");
    }

    [Fact]
    public async Task TryEvictTagsAsync_EvictsUnderNoneSoADisconnectedClientDoesNotAbandonTheCleanup()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = new Mock<IOutputCacheStore>();

        await store.Object.TryEvictTagsAsync(NullLogger.Instance, "catalog:products");

        store.Verify(s => s.EvictByTagAsync("catalog:products", CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task TryEvictTagsAsync_SwallowsAStoreFailureAndKeepsGoing()
    {
        var store = new Mock<IOutputCacheStore>();
        store.Setup(s => s.EvictByTagAsync("catalog:products", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var act = async () => await store.Object.TryEvictTagsAsync(
            NullLogger.Instance, "catalog:products", "catalog");

        await act.Should().NotThrowAsync(
            "the mutation has already committed, so a cache outage must not turn a successful write into a client-visible error");
        store.Verify(s => s.EvictByTagAsync("catalog", It.IsAny<CancellationToken>()), Times.Once,
            "a failure on one tag must not skip the rest");
    }

    [Fact]
    public async Task NullStore_IsRejected()
    {
        IOutputCacheStore store = null!;

        var evict = async () => await store.EvictTagsAsync(CancellationToken.None, "tag");
        var tryEvict = async () => await store.TryEvictTagsAsync(NullLogger.Instance, "tag");

        await evict.Should().ThrowAsync<ArgumentNullException>();
        await tryEvict.Should().ThrowAsync<ArgumentNullException>();
    }
}
