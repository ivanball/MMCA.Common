using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Sessions;

/// <summary>
/// The rotation-race grace on <see cref="AuthSessionIssuer.RotateAsync"/>
/// (<see cref="RefreshSessionSettings.ReuseGraceSeconds"/>, default 10s, matching the cookie refresher's
/// rotation grace). Two tabs, or two replicas serving one browser, can present the same refresh token
/// a moment apart; the second one used to be answered as a replay and sign the user out everywhere
/// (BR-206). A token that was rotated <b>within</b> the grace now gets a transient 409 (Conflict) and
/// the family stays live, on both paths a spent token can arrive by: the lookup that finds the row
/// already revoked, and the loser of the <see cref="IRefreshSessionStore.TryRotateAsync"/> claim (which
/// must re-read the row untracked, since its own tracked copy still shows it live). Anything else that
/// is already revoked keeps the BR-206 family revocation.
/// </summary>
public sealed class AuthSessionIssuerReuseGraceTests
{
    private const UserIdentifierType UserId = 1;
    private const string PresentedToken = "stored-refresh";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = FixedNow.UtcDateTime;

    [Fact]
    public void ReuseGraceSeconds_DefaultsToTenSeconds() =>
        new RefreshSessionSettings().ReuseGraceSeconds.Should().Be(10, "it matches CookieSessionRefresher.RotationGrace");

    // (a) The loser of the claim, with the winner's rotation a moment ago: a 409, nothing revoked.
    [Fact]
    public async Task RotateAsync_WhenTheRotationLosesTheRaceWithinTheGrace_ReturnsConflict_AndKeepsTheFamily()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        var otherDevice = store.SeedLive("phone-token");
        store.RotationClaimed = false;
        store.SetUntrackedView(RevokedCopyOf(presented, Now.AddSeconds(-2), RefreshSession.ReasonRotated, "winner-refresh"));

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Conflict, "a 409 tells the client to retry with the winner's cookie");
        otherDevice.IsRevoked.Should().BeFalse("a lost race inside the grace is not token reuse");
        presented.ReasonRevoked.Should().NotBe(RefreshSession.ReasonReuseDetected);
        store.FamilyReads.Should().Be(0, "no family revocation runs for a rotation race");
        store.UntrackedReads.Should().BeGreaterThan(0, "the loser's tracked copy still shows the row live, so it must re-read it");
    }

    // (b) The loser of the claim, but the row was rotated longer ago than the grace: a replay (BR-206).
    [Fact]
    public async Task RotateAsync_WhenTheRotationLosesTheRaceOutsideTheGrace_RevokesTheFamily()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        var otherDevice = store.SeedLive("phone-token");
        store.RotationClaimed = false;
        store.SetUntrackedView(RevokedCopyOf(presented, Now.AddSeconds(-30), RefreshSession.ReasonRotated, "winner-refresh"));

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken" && e.Type == ErrorType.Unauthorized);
        otherDevice.IsRevoked.Should().BeTrue("a token rotated past the grace coming back is reuse");
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    // The loser re-reads the row and finds it revoked for a reason other than rotation: still BR-206.
    [Fact]
    public async Task RotateAsync_WhenTheRotationLosesTheRaceToAReuseRevocation_RevokesTheFamily()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        var otherDevice = store.SeedLive("phone-token");
        store.RotationClaimed = false;
        store.SetUntrackedView(RevokedCopyOf(presented, Now.AddSeconds(-2), RefreshSession.ReasonReuseDetected, replacedBy: null));

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        otherDevice.IsRevoked.Should().BeTrue();
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    // (c) The token was already rotated a moment ago (the lookup finds the revoked row): a 409.
    [Fact]
    public async Task RotateAsync_WhenTheTokenWasRotatedWithinTheGrace_ReturnsConflict_AndKeepsTheFamily()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        presented.Revoke(Now.AddSeconds(-3), RefreshSession.ReasonRotated, RefreshSession.HashToken("successor-refresh"));
        var otherDevice = store.SeedLive("phone-token");

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Conflict);
        otherDevice.IsRevoked.Should().BeFalse("a sibling request a moment behind the rotation is not a stolen token");
        presented.ReasonRevoked.Should().Be(RefreshSession.ReasonRotated, "the original revocation is kept");
        store.FamilyReads.Should().Be(0);
        store.Added.Should().BeEmpty("nothing new is minted for the spent token");
    }

    [Fact]
    public async Task RotateAsync_WhenTheTokenWasRotatedOutsideTheGrace_RevokesTheFamily()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        presented.Revoke(Now.AddSeconds(-11), RefreshSession.ReasonRotated, RefreshSession.HashToken("successor-refresh"));
        var otherDevice = store.SeedLive("phone-token");

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken" && e.Type == ErrorType.Unauthorized);
        otherDevice.IsRevoked.Should().BeTrue();
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    // (d) Already revoked as reuse, however recently: the grace covers rotation only.
    [Fact]
    public async Task RotateAsync_WhenTheTokenWasFlaggedAsReuseWithinTheGrace_StillRevokesTheFamily()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        presented.Revoke(Now.AddSeconds(-1), RefreshSession.ReasonReuseDetected);
        var otherDevice = store.SeedLive("phone-token");

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        otherDevice.IsRevoked.Should().BeTrue();
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    // A signed-out session is still not a theft signal inside the grace: that request alone fails.
    [Fact]
    public async Task RotateAsync_WhenTheTokenWasSignedOutWithinTheGrace_FailsAlone_WithoutAConflict()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        presented.Revoke(Now.AddSeconds(-1), RefreshSession.ReasonSignedOut);
        var otherDevice = store.SeedLive("phone-token");

        Result<AuthenticationResponse> result = await RotateAsync(store);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken" && e.Type == ErrorType.Unauthorized);
        otherDevice.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task RotateAsync_HonoursAConfiguredGrace()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        presented.Revoke(Now.AddSeconds(-20), RefreshSession.ReasonRotated, RefreshSession.HashToken("successor-refresh"));
        var otherDevice = store.SeedLive("phone-token");

        Result<AuthenticationResponse> result = await RotateAsync(store, new RefreshSessionSettings { ReuseGraceSeconds = 30 });

        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Conflict);
        otherDevice.IsRevoked.Should().BeFalse();
    }

    [Fact]
    public async Task RotateAsync_WithTheGraceSetToZero_TreatsEveryRotatedTokenAsReuse()
    {
        var store = new RaceStore();
        var presented = store.SeedLive(PresentedToken);
        presented.Revoke(Now.AddSeconds(-1), RefreshSession.ReasonRotated, RefreshSession.HashToken("successor-refresh"));
        var otherDevice = store.SeedLive("phone-token");

        Result<AuthenticationResponse> result = await RotateAsync(store, new RefreshSessionSettings { ReuseGraceSeconds = 0 });

        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        otherDevice.IsRevoked.Should().BeTrue();
    }

    private static Task<Result<AuthenticationResponse>> RotateAsync(RaceStore store, RefreshSessionSettings? settings = null)
    {
        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(t => t.GenerateRefreshToken()).Returns("refresh-1");
        var sut = new AuthSessionIssuer(
            tokenService.Object,
            store,
            Options.Create(settings ?? new RefreshSessionSettings()),
            new FixedTimeProvider(FixedNow));

        return sut.RotateAsync(UserId, PresentedToken, _ => "access-1", ipAddress: null, userAgent: null, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// What a fresh, untracked read of the row returns: same id and token, revoked by someone else.
    /// </summary>
    private static RefreshSession RevokedCopyOf(RefreshSession tracked, DateTime revokedAt, string reason, string? replacedBy)
    {
        var copy = new RefreshSession
        {
            Id = tracked.Id,
            UserId = tracked.UserId,
            TokenHash = tracked.TokenHash,
            CreatedAt = tracked.CreatedAt,
            ExpiresAt = tracked.ExpiresAt,
        };
        copy.Revoke(revokedAt, reason, replacedBy is null ? null : RefreshSession.HashToken(replacedBy));
        return copy;
    }

    /// <summary>
    /// A store that behaves like the EF one under a race: lookups hand out the TRACKED instances, which
    /// a concurrent writer's change does not reach, while <see cref="FindByIdUntrackedAsync"/> returns
    /// what the database holds now.
    /// </summary>
    private sealed class RaceStore : IRefreshSessionStore
    {
        private readonly List<RefreshSession> _sessions = [];
        private readonly List<RefreshSession> _added = [];
        private readonly Dictionary<Guid, RefreshSession> _untracked = [];

        public bool RotationClaimed { get; set; } = true;

        public int FamilyReads { get; private set; }

        public int UntrackedReads { get; private set; }

        public IReadOnlyList<RefreshSession> Added => _added;

        public RefreshSession SeedLive(string token)
        {
            var session = RefreshSession.Create(UserId, token, Now.AddHours(-1), Now.AddDays(6)).Value!;
            _sessions.Add(session);
            return session;
        }

        public void SetUntrackedView(RefreshSession stored) => _untracked[stored.Id] = stored;

        public Task AddAsync(RefreshSession session, CancellationToken cancellationToken = default)
        {
            _added.Add(session);
            return Task.CompletedTask;
        }

        public Task<RefreshSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions.Find(s => string.Equals(s.TokenHash, tokenHash, StringComparison.Ordinal)));

        public Task<IReadOnlyList<RefreshSession>> GetUnrevokedByUserAsync(
            UserIdentifierType userId,
            CancellationToken cancellationToken = default)
        {
            FamilyReads++;
            IReadOnlyList<RefreshSession> live = [.. _sessions.Where(s => s.UserId == userId && !s.IsRevoked)];
            return Task.FromResult(live);
        }

        public Task<RefreshSession?> FindByIdAsync(
            Guid id,
            UserIdentifierType userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions.Find(s => s.Id == id && s.UserId == userId));

        public Task<RefreshSession?> FindByIdUntrackedAsync(Guid id, CancellationToken cancellationToken = default)
        {
            UntrackedReads++;
            return Task.FromResult(_untracked.TryGetValue(id, out var stored) ? stored : _sessions.Find(s => s.Id == id));
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public async Task<bool> TryRotateAsync(
            RefreshSession presented,
            RefreshSession successor,
            DateTime revokedAt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(presented);
            ArgumentNullException.ThrowIfNull(successor);

            if (!RotationClaimed)
            {
                // The database arbitrated for the other request; this one writes nothing and its
                // tracked copy of the presented row is left exactly as it was read.
                return false;
            }

            presented.Revoke(revokedAt, RefreshSession.ReasonRotated, successor.TokenHash);
            await AddAsync(successor, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
