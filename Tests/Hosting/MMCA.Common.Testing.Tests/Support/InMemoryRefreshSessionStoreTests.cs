using AwesomeAssertions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Testing.Support;
using Xunit;

namespace MMCA.Common.Testing.Tests.Support;

/// <summary>
/// <see cref="InMemoryRefreshSessionStore.CountLiveSessionsByUserAsync"/> mirrors the EF store: it
/// counts only the requested users' sessions that are live at the given instant (not revoked, not
/// expired, and persisted rather than merely staged), and leaves a user with none out of the answer.
/// </summary>
public sealed class InMemoryRefreshSessionStoreTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryRefreshSessionStore _store = new();

    [Fact]
    public async Task CountLiveSessionsByUserAsync_CountsEachRequestedUsersLiveSessions()
    {
        _store.SeedLive(1, "token-1a", Now.AddHours(-2), Now.AddDays(1));
        _store.SeedLive(1, "token-1b", Now.AddHours(-1), Now.AddDays(1));
        _store.SeedLive(2, "token-2a", Now.AddHours(-1), Now.AddDays(1));

        var counts = await _store.CountLiveSessionsByUserAsync([1, 2], Now, TestContext.Current.CancellationToken);

        counts.Should().HaveCount(2);
        counts[1].Should().Be(2);
        counts[2].Should().Be(1);
    }

    [Fact]
    public async Task CountLiveSessionsByUserAsync_IgnoresUsersThatWereNotRequested()
    {
        _store.SeedLive(1, "token-1", Now.AddHours(-1), Now.AddDays(1));
        _store.SeedLive(3, "token-3", Now.AddHours(-1), Now.AddDays(1));

        var counts = await _store.CountLiveSessionsByUserAsync([1], Now, TestContext.Current.CancellationToken);

        counts.Keys.Should().Equal(1);
    }

    [Fact]
    public async Task CountLiveSessionsByUserAsync_LeavesOutARequestedUserWithNoLiveSession()
    {
        _store.SeedLive(1, "token-1", Now.AddHours(-1), Now.AddDays(1));

        var counts = await _store.CountLiveSessionsByUserAsync([1, 2], Now, TestContext.Current.CancellationToken);

        counts.Should().ContainKey(1).And.NotContainKey(2, "a user with no live session has no entry, not a zero");
    }

    [Fact]
    public async Task CountLiveSessionsByUserAsync_SkipsExpiredAndRevokedSessions()
    {
        _store.SeedLive(1, "live", Now.AddHours(-1), Now.AddDays(1));
        _store.SeedLive(1, "expired", Now.AddDays(-3), Now.AddDays(-1));
        _store.SeedLive(1, "expires-now", Now.AddHours(-1), Now);
        var revoked = _store.SeedLive(1, "revoked", Now.AddHours(-1), Now.AddDays(1));
        revoked.Revoke(Now.AddMinutes(-5), RefreshSession.ReasonSignedOut).IsSuccess.Should().BeTrue();

        var counts = await _store.CountLiveSessionsByUserAsync([1], Now, TestContext.Current.CancellationToken);

        counts[1].Should().Be(1, "only the unrevoked session whose expiry is still ahead is live");
    }

    [Fact]
    public async Task CountLiveSessionsByUserAsync_DoesNotSeeAStagedSessionUntilItIsSaved()
    {
        var staged = RefreshSession.Create(1, "staged", Now.AddMinutes(-1), Now.AddDays(1)).Value!;
        await _store.AddAsync(staged, TestContext.Current.CancellationToken);

        var before = await _store.CountLiveSessionsByUserAsync([1], Now, TestContext.Current.CancellationToken);
        await _store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var after = await _store.CountLiveSessionsByUserAsync([1], Now, TestContext.Current.CancellationToken);

        before.Should().BeEmpty("EF does not read the change tracker from a database query");
        after[1].Should().Be(1);
    }

    [Fact]
    public async Task CountLiveSessionsByUserAsync_WithNoRequestedUsers_ReturnsEmpty()
    {
        _store.SeedLive(1, "token-1", Now.AddHours(-1), Now.AddDays(1));

        var counts = await _store.CountLiveSessionsByUserAsync([], Now, TestContext.Current.CancellationToken);

        counts.Should().BeEmpty();
    }

    [Fact]
    public async Task CountLiveSessionsByUserAsync_WithNullUserIds_Throws()
    {
        var act = () => _store.CountLiveSessionsByUserAsync(null!, Now, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
