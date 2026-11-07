using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.ViewAs;

/// <summary>
/// Spec A item 6 / spec B item 2: every view-as grant on an account appears in that account's
/// sign-in history as a <see cref="SignInHistoryEntryKind.ViewAs"/> row ("Viewed by support"), in
/// the holder's own history and in the administrator's view of it.
/// </summary>
public sealed class ViewAsSignInHistoryTests
{
    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly InMemoryRefreshSessionStore _sessions = new();
    private readonly InMemoryViewAsGrantStore _grants = new();

    [Fact]
    public async Task ListSignInsAsync_AViewAsGrantOnTheAccount_AppearsAsAViewedBySupportRow()
    {
        var ended = SeedGrant(Now.UtcDateTime.AddHours(-2));
        ended.End(Now.UtcDateTime.AddHours(-2).AddMinutes(4));
        var live = SeedGrant(Now.UtcDateTime.AddMinutes(-3));
        _grants.Seed(ViewAsGrant.Create(StaffUserId, 3, "Another account entirely", Now.UtcDateTime, TimeSpan.FromMinutes(15)).Value!);

        var issuer = new AuthSessionIssuer(
            Mock.Of<ITokenService>(),
            _sessions,
            Options.Create(new RefreshSessionSettings()),
            _clock,
            logger: null,
            viewAsGrants: _grants);

        var history = await issuer.ListSignInsAsync(TargetUserId, currentSessionId: null, CancellationToken.None);

        history.Should().HaveCount(2, "only the account's own grants appear");
        history.Should().OnlyContain(row => row.Kind == SignInHistoryEntryKind.ViewAs && !row.IsCurrent && row.IpAddress == null && row.UserAgent == null);

        var newest = history[0];
        newest.SignInId.Should().Be(live.Id);
        newest.StartedAt.Should().Be(live.GrantedAt);
        newest.EndedAt.Should().BeNull("the grant is still live");
        newest.EndReason.Should().BeNull();

        var older = history[1];
        older.SignInId.Should().Be(ended.Id);
        older.EndedAt.Should().Be(ended.EndedAt);
        older.EndReason.Should().Be(SignInEndReasons.ViewAsEnded);
    }

    [Fact]
    public async Task GetSignInsAsync_TheAdministratorsViewShowsTheSameRows_AndAnExpiredGrantEndsExpired()
    {
        var expired = SeedGrant(Now.UtcDateTime.AddDays(-1));
        _sessions.SeedLive(TargetUserId, "live-refresh-token", Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddDays(6));

        var service = new UserSessionsAdministrationService(_sessions, _clock, _grants);

        var history = (await service.GetSignInsAsync(TargetUserId)).Value!;

        history.Should().HaveCount(2);
        history[0].Kind.Should().Be(SignInHistoryEntryKind.SignIn, "the sign-in is newer than the grant");
        var row = history[1];
        row.Kind.Should().Be(SignInHistoryEntryKind.ViewAs);
        row.EndedAt.Should().Be(expired.ExpiresAt);
        row.EndReason.Should().Be(SignInEndReasons.Expired);
    }

    private ViewAsGrant SeedGrant(DateTime grantedAt)
    {
        var grant = ViewAsGrant.Create(StaffUserId, TargetUserId, "Ticket 4411: schedule missing", grantedAt, TimeSpan.FromMinutes(15)).Value!;
        _grants.Seed(grant);
        return grant;
    }
}
