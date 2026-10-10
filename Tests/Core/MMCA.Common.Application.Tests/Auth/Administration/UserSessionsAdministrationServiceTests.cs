using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Application.Tests.Auth.Administration;

/// <summary>
/// Pins <see cref="UserSessionsAdministrationService"/>, the administrator's read-only view of another
/// account's signed-in devices. "Signed in" means a LIVE refresh session: revoked and expired rows are
/// excluded (the store returns expired-but-unrevoked rows on purpose), another user's sessions never
/// appear, the list is newest first, and no row is ever marked current because the administrator is
/// not on the viewed user's device.
/// </summary>
public sealed class UserSessionsAdministrationServiceTests
{
    private const UserIdentifierType UserA = 42;
    private const UserIdentifierType UserB = 43;
    private const UserIdentifierType UserWithNoSessions = 44;

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = FixedNow.UtcDateTime;

    private readonly InMemoryRefreshSessionStore _store = new();
    private readonly FakeTimeProvider _time = new(FixedNow);

    private RefreshSession _olderLive = null!;
    private RefreshSession _newerLive = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserSessionsAdministrationService CreateSut() => new(_store, _time);

    private RefreshSession Seed(
        UserIdentifierType userId,
        string token,
        DateTime createdAt,
        DateTime expiresAt,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var session = RefreshSession.Create(userId, token, createdAt, expiresAt, ipAddress, userAgent).Value!;
        _store.Seed(session);
        return session;
    }

    /// <summary>
    /// User A: two live sessions (seeded oldest first, so newest-first ordering is the service's own
    /// work), one revoked, one expired but never revoked. User B: one live session.
    /// </summary>
    private void ArrangeMixedSessions()
    {
        _olderLive = Seed(UserA, "a-older-live", Now.AddDays(-3), Now.AddDays(27), "203.0.113.7", "Mozilla/5.0 (Windows NT 10.0) Chrome/126.0.0.0");
        _newerLive = Seed(UserA, "a-newer-live", Now.AddHours(-2), Now.AddDays(30), "198.51.100.4", "AtlDevCon/1.9.2 (Android 15; MmcaApp)");

        var revoked = Seed(UserA, "a-revoked", Now.AddDays(-1), Now.AddDays(29), "192.0.2.1", "Firefox/127.0");
        revoked.Revoke(Now.AddMinutes(-30), RefreshSession.ReasonRotated).IsSuccess.Should().BeTrue();

        Seed(UserA, "a-expired", Now.AddDays(-40), Now.AddDays(-10), "192.0.2.2", "Safari/605.1.15");

        Seed(UserB, "b-live", Now.AddHours(-1), Now.AddDays(30), "192.0.2.3", "Opera");
    }

    [Fact]
    public async Task GetSessionsAsync_ReturnsOnlyTheUsersLiveSessions()
    {
        ArrangeMixedSessions();

        var result = await CreateSut().GetSessionsAsync(UserA, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Select(s => s.SessionId).Should().BeEquivalentTo(
            [_newerLive.Id, _olderLive.Id],
            "revoked and expired sessions are not signed in, and user B's session is not user A's");
    }

    [Fact]
    public async Task GetSessionsAsync_OrdersNewestFirst()
    {
        ArrangeMixedSessions();

        var result = await CreateSut().GetSessionsAsync(UserA, Ct);

        result.Value!.Select(s => s.SessionId).Should().Equal(_newerLive.Id, _olderLive.Id);
    }

    [Fact]
    public async Task GetSessionsAsync_NeverMarksASessionCurrent()
    {
        ArrangeMixedSessions();

        var result = await CreateSut().GetSessionsAsync(UserA, Ct);

        result.Value!.Should().NotBeEmpty();
        result.Value!.Should().OnlyContain(s => !s.IsCurrent);
    }

    [Fact]
    public async Task GetSessionsAsync_PassesTheRecordedDetailsThrough()
    {
        ArrangeMixedSessions();

        var result = await CreateSut().GetSessionsAsync(UserA, Ct);

        var newest = result.Value![0];
        newest.SessionId.Should().Be(_newerLive.Id);
        newest.CreatedAt.Should().Be(_newerLive.CreatedAt);
        newest.ExpiresAt.Should().Be(_newerLive.ExpiresAt);
        newest.IpAddress.Should().Be("198.51.100.4");
        newest.UserAgent.Should().Be("AtlDevCon/1.9.2 (Android 15; MmcaApp)");

        var oldest = result.Value![1];
        oldest.IpAddress.Should().Be("203.0.113.7");
        oldest.UserAgent.Should().Be("Mozilla/5.0 (Windows NT 10.0) Chrome/126.0.0.0");
    }

    [Fact]
    public async Task GetSessionsAsync_UsesTheInjectedClockToDecideWhatHasExpired()
    {
        ArrangeMixedSessions();

        // Move past the older live session's expiry: it is no longer signed in.
        _time.Advance(TimeSpan.FromDays(28));

        var result = await CreateSut().GetSessionsAsync(UserA, Ct);

        result.Value!.Select(s => s.SessionId).Should().Equal(_newerLive.Id);
    }

    [Fact]
    public async Task GetSessionsAsync_ForTheOtherUser_ReturnsOnlyTheirSession()
    {
        ArrangeMixedSessions();

        var result = await CreateSut().GetSessionsAsync(UserB, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().ContainSingle().Which.IpAddress.Should().Be("192.0.2.3");
    }

    [Fact]
    public async Task GetSessionsAsync_ForAUserWithNoSessions_SucceedsWithAnEmptyList()
    {
        ArrangeMixedSessions();

        var result = await CreateSut().GetSessionsAsync(UserWithNoSessions, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }
}
