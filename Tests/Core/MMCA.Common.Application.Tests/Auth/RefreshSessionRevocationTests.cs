using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Covers the public "evict every live session" step an app-side user administration service runs
/// after locking an account or changing its role: a host with no session store is a no-op, a user
/// with no live session causes no save, and otherwise every un-revoked session of that user (and only
/// that user) is revoked at the clock's instant and saved once.
/// </summary>
public sealed class RefreshSessionRevocationTests
{
    private const UserIdentifierType UserId = 7;
    private const UserIdentifierType OtherUserId = 8;

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTime Now = FixedNow.UtcDateTime;

    [Fact]
    public async Task RevokeAllAsync_WithNoStore_IsANoOp()
    {
        Func<Task> act = () => RefreshSessionRevocation.RevokeAllAsync(
            refreshSessions: null,
            new FakeTimeProvider(FixedNow),
            UserId,
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RevokeAllAsync_WithZeroSessions_DoesNotSave()
    {
        var store = new InMemoryRefreshSessionStore();
        var otherUsersSession = store.SeedLive(OtherUserId, "other", Now.AddHours(-1), Now.AddDays(5));

        await RefreshSessionRevocation.RevokeAllAsync(store, new FakeTimeProvider(FixedNow), UserId, CancellationToken.None);

        store.SaveCount.Should().Be(0, "a user holding no un-revoked session needs no write");
        otherUsersSession.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAllAsync_WithLiveSessions_RevokesEachAtTheClockInstantAndSavesOnce()
    {
        var store = new InMemoryRefreshSessionStore();
        var first = store.SeedLive(UserId, "first", Now.AddDays(-2), Now.AddDays(5));
        var second = store.SeedLive(UserId, "second", Now.AddHours(-3), Now.AddDays(6));
        var otherUsersSession = store.SeedLive(OtherUserId, "other", Now.AddHours(-1), Now.AddDays(5));

        await RefreshSessionRevocation.RevokeAllAsync(store, new FakeTimeProvider(FixedNow), UserId, CancellationToken.None);

        first.IsRevoked.Should().BeTrue();
        second.IsRevoked.Should().BeTrue();
        first.RevokedAt.Should().Be(Now);
        second.RevokedAt.Should().Be(Now);
        first.ReasonRevoked.Should().Be(RefreshSession.ReasonSignedOut);
        second.ReasonRevoked.Should().Be(RefreshSession.ReasonSignedOut);
        otherUsersSession.IsRevoked.Should().BeFalse("only the named user's sessions are evicted");
        store.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task RevokeAllAsync_WithNoTimeProvider_FallsBackToTheSystemClock()
    {
        var store = new InMemoryRefreshSessionStore();
        var session = store.SeedLive(UserId, "token", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(5));
        var before = DateTime.UtcNow;

        await RefreshSessionRevocation.RevokeAllAsync(store, timeProvider: null, UserId, CancellationToken.None);

        session.RevokedAt.Should().NotBeNull();
        session.RevokedAt!.Value.Should().BeOnOrAfter(before);
        store.SaveCount.Should().Be(1);
    }
}
