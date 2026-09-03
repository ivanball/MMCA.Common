using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.UI.Pages.Common;
using MMCA.Common.UI.Services.Capabilities.DeviceStatus;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// Unit tests for <see cref="OfflineFirstPageSnapshot{TItem}"/>: the last-known-good first page a
/// list surface falls back to on a dead network (ADR-042). The guard rails matter more than the
/// happy path here: only page 1 is ever remembered or served, an online device never reads the
/// snapshot, and a head with no cache store stays completely inert.
/// </summary>
public sealed class OfflineFirstPageSnapshotTests
{
    private const string CacheKey = "tests.sessions.page1";

    private static readonly IReadOnlyList<string> Page = ["Alpha", "Bravo"];

    [Fact]
    public async Task WhenOffline_ServesTheRememberedFirstPage()
    {
        var store = new FakeLocalCacheStore();
        var snapshot = Build(store, online: false);

        await snapshot.RememberAsync((Page, 7), page: 1, Xunit.TestContext.Current.CancellationToken);
        var cached = await snapshot.TryReadAsync(page: 1, Xunit.TestContext.Current.CancellationToken);

        cached.Should().NotBeNull();
        cached!.Value.Items.Should().Equal(Page);
        cached.Value.TotalItems.Should().Be(7);
    }

    [Fact]
    public async Task WhenOnline_NeverServesTheSnapshot()
    {
        var store = new FakeLocalCacheStore();
        var offline = Build(store, online: false);
        await offline.RememberAsync((Page, 7), page: 1, Xunit.TestContext.Current.CancellationToken);

        var online = Build(store, online: true);

        online.CanServe(1).Should().BeFalse();
        (await online.TryReadAsync(page: 1, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task OnlyTheFirstPageIsRemembered()
    {
        var store = new FakeLocalCacheStore();
        var snapshot = Build(store, online: false);

        await snapshot.RememberAsync((Page, 7), page: 2, Xunit.TestContext.Current.CancellationToken);

        store.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyTheFirstPageIsServed()
    {
        var store = new FakeLocalCacheStore();
        var snapshot = Build(store, online: false);
        await snapshot.RememberAsync((Page, 7), page: 1, Xunit.TestContext.Current.CancellationToken);

        snapshot.CanServe(2).Should().BeFalse();
        (await snapshot.TryReadAsync(page: 2, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task WithoutACacheStore_StaysInert()
    {
        // Blazor Server reports the store unavailable: SSR always has the live API, so nothing is
        // written and nothing is ever served.
        var store = new FakeLocalCacheStore { IsAvailable = false };
        var snapshot = Build(store, online: false);

        await snapshot.RememberAsync((Page, 7), page: 1, Xunit.TestContext.Current.CancellationToken);

        store.Entries.Should().BeEmpty();
        snapshot.CanServe(1).Should().BeFalse();
        (await snapshot.TryReadAsync(page: 1, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task WithNothingCached_ReturnsNull()
    {
        var snapshot = Build(new FakeLocalCacheStore(), online: false);

        (await snapshot.TryReadAsync(page: 1, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task SnapshotsAreKeyedPerSurface()
    {
        // Two list surfaces sharing one store must not read each other's rows.
        var store = new FakeLocalCacheStore();
        var sessions = new OfflineFirstPageSnapshot<string>(store, new FakeConnectivity(false), "tests.sessions");
        var speakers = new OfflineFirstPageSnapshot<string>(store, new FakeConnectivity(false), "tests.speakers");

        await sessions.RememberAsync((Page, 7), page: 1, Xunit.TestContext.Current.CancellationToken);

        (await speakers.TryReadAsync(page: 1, Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private static OfflineFirstPageSnapshot<string> Build(ILocalCacheStore store, bool online) =>
        new(store, new FakeConnectivity(online), CacheKey);

    /// <summary>
    /// In-memory <see cref="ILocalCacheStore"/> that JSON round-trips every value, so the snapshot's
    /// own cached-page shape is genuinely exercised rather than handed back by reference.
    /// </summary>
    private sealed class FakeLocalCacheStore : ILocalCacheStore
    {
        public Dictionary<string, string> Entries { get; } = [];

        public bool IsAvailable { get; init; } = true;

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            Entries[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConnectivity(bool isOnline) : IConnectivityStatusService
    {
#pragma warning disable CS0067 // The snapshot never subscribes; the event exists only to satisfy the contract.
        public event EventHandler? ConnectivityChanged;
#pragma warning restore CS0067

        public bool IsOnline => isOnline;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
