using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Sessions;

/// <summary>
/// Sign-in history on <see cref="AuthSessionIssuer.ListSignInsAsync"/> (T13, ADR-097 amendment): one
/// row per sign-in, however often its refresh token rotated, with how it ended; and the reuse
/// detection Warning (BR-206), which used to revoke a family silently.
/// </summary>
public sealed class AuthSessionIssuerSignInHistoryTests
{
    private const UserIdentifierType UserId = 1;
    private const UserIdentifierType OtherUserId = 2;
    private const string IpAddress = "203.0.113.7";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Firefox/131.0";

    private static readonly DateTimeOffset Start = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    // Spec A assertion 1: a sign-in rotated N times is ONE row spanning the first and last CreatedAt.
    [Fact]
    public async Task ListSignInsAsync_ASignInRotatedNTimes_ReturnsOneRowFromTheFirstToTheLastCreatedAt()
    {
        var harness = new Harness();
        var token = await harness.SignInAsync(UserId);

        for (var rotation = 0; rotation < 3; rotation++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(20));
            token = await harness.RotateAsync(UserId, token);
        }

        var lastRotatedAt = harness.Clock.GetUtcNow().UtcDateTime;

        var signIns = await harness.Sut.ListSignInsAsync(UserId, harness.LastSessionId, CancellationToken.None);

        var signIn = signIns.Should().ContainSingle("four rows of one rotation chain are one sign-in").Subject;
        signIn.StartedAt.Should().Be(Start.UtcDateTime);
        signIn.LastActiveAt.Should().Be(lastRotatedAt);
        signIn.EndedAt.Should().BeNull("the chain's head is still live");
        signIn.EndReason.Should().BeNull();
        signIn.IsCurrent.Should().BeTrue("the caller's sid is the chain's head");
        signIn.IpAddress.Should().Be(IpAddress);
        signIn.UserAgent.Should().Be(UserAgent);
        signIn.Kind.Should().Be(SignInHistoryEntryKind.SignIn);
        harness.Store.Sessions.Should().HaveCount(4);
    }

    // Spec A assertion 2 (lookup path): a replayed rotated token revokes the family, the history
    // says ReuseDetected, and exactly one Warning is written.
    [Fact]
    public async Task ListSignInsAsync_AFamilyRevokedByReuse_EndsWithReuseDetected_AndExactlyOneWarningIsLogged()
    {
        var harness = new Harness();
        var first = await harness.SignInAsync(UserId);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.RotateAsync(UserId, first);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        var replay = await harness.Sut.RotateAsync(UserId, first, _ => "access", IpAddress, UserAgent, CancellationToken.None);

        replay.IsFailure.Should().BeTrue("a rotated token presented past the grace is a replay");
        var signIn = (await harness.Sut.ListSignInsAsync(UserId, null, CancellationToken.None))
            .Should().ContainSingle().Subject;
        signIn.EndReason.Should().Be(SignInEndReasons.ReuseDetected);
        signIn.EndedAt.Should().Be(harness.Clock.GetUtcNow().UtcDateTime);
        harness.Logs.Where(l => l.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("reuse");
    }

    // Spec A assertion 2 (rotation-claim race path): the loser of a claim outside the grace is
    // answered as a replay, and that path logs the same single Warning.
    [Fact]
    public async Task RotateAsync_WhenALostRotationClaimIsAnsweredAsReuse_LogsExactlyOneWarning()
    {
        var harness = new Harness();
        var token = await harness.SignInAsync(UserId);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        harness.Store.RotationOutcome = () => false;

        var result = await harness.Sut.RotateAsync(UserId, token, _ => "access", IpAddress, UserAgent, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        var signIn = (await harness.Sut.ListSignInsAsync(UserId, null, CancellationToken.None))
            .Should().ContainSingle().Subject;
        signIn.EndReason.Should().Be(SignInEndReasons.ReuseDetected);
        harness.Logs.Where(l => l.Level == LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain("reuse");
    }

    [Fact]
    public async Task RotateAsync_ReuseWarning_CarriesTheUserAndRevokedCount_AndNoClientMetadata()
    {
        var harness = new Harness();
        var first = await harness.SignInAsync(UserId);
        await harness.SignInAsync(UserId);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.RotateAsync(UserId, first);
        harness.Clock.Advance(TimeSpan.FromMinutes(5));

        await harness.Sut.RotateAsync(UserId, first, _ => "access", IpAddress, UserAgent, CancellationToken.None);

        var warning = harness.Logs.Single(l => l.Level == LogLevel.Warning).Message;
        warning.Should().Contain($"user {UserId}").And.Contain("2 live session");
        warning.Should().NotContain(IpAddress).And.NotContain("Firefox");
    }

    // Spec A assertion 4 (service half): the history is scoped to the requested user.
    [Fact]
    public async Task ListSignInsAsync_NeverReturnsAnotherUsersSignIns()
    {
        var harness = new Harness();
        await harness.SignInAsync(OtherUserId);
        await harness.SignInAsync(UserId);
        var ownSignInId = harness.LastSessionId;
        await harness.SignInAsync(OtherUserId);

        var signIns = await harness.Sut.ListSignInsAsync(UserId, null, CancellationToken.None);

        signIns.Should().ContainSingle().Which.SignInId.Should().Be(ownSignInId!.Value);
    }

    [Fact]
    public async Task ListSignInsAsync_ReportsHowEachSignInEnded_NewestFirst()
    {
        var harness = new Harness();
        var signedOut = await harness.SignInAsync(UserId);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.SignInAsync(UserId);
        var live = harness.LastSessionId;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.Sut.SignOutAsync(UserId, signedOut, CancellationToken.None);
        var signedOutAt = harness.Clock.GetUtcNow().UtcDateTime;

        var signIns = await harness.Sut.ListSignInsAsync(UserId, live, CancellationToken.None);

        signIns.Should().HaveCount(2);
        signIns[0].IsCurrent.Should().BeTrue("newest first, and the newer sign-in is the caller's");
        signIns[0].EndReason.Should().BeNull();
        signIns[1].EndReason.Should().Be(SignInEndReasons.SignedOut);
        signIns[1].EndedAt.Should().Be(signedOutAt);
    }

    [Fact]
    public async Task ListSignInsAsync_AnExpiredSignIn_EndsAtItsExpiryAsExpired()
    {
        var harness = new Harness();
        await harness.SignInAsync(UserId);
        harness.Clock.Advance(TimeSpan.FromDays(8));

        var signIn = (await harness.Sut.ListSignInsAsync(UserId, null, CancellationToken.None))
            .Should().ContainSingle().Subject;

        signIn.EndReason.Should().Be(SignInEndReasons.Expired);
        signIn.EndedAt.Should().Be(Start.UtcDateTime.AddDays(7));
    }

    // Rows written before the SignInId column shipped carry null: each one that was not rotated away
    // is its own sign-in, and a rotated one is hidden (its successor stands for it).
    [Fact]
    public async Task ListSignInsAsync_LegacyRowsWithoutASignInId_ShowEachUnrotatedRowAsItsOwnSignIn()
    {
        var harness = new Harness();
        var now = Start.UtcDateTime;
        var rotatedLegacy = Legacy(UserId, "legacy-1", now.AddDays(-2));
        rotatedLegacy.Revoke(now.AddDays(-1), RefreshSession.ReasonRotated, RefreshSession.HashToken("legacy-2"));
        var liveLegacy = Legacy(UserId, "legacy-2", now.AddDays(-1));
        var signedOutLegacy = Legacy(UserId, "legacy-3", now.AddDays(-3));
        signedOutLegacy.Revoke(now.AddDays(-3).AddHours(1), RefreshSession.ReasonSignedOut);
        harness.Store.Seed(rotatedLegacy);
        harness.Store.Seed(liveLegacy);
        harness.Store.Seed(signedOutLegacy);

        var signIns = await harness.Sut.ListSignInsAsync(UserId, null, CancellationToken.None);

        signIns.Select(s => s.SignInId).Should().Equal(liveLegacy.Id, signedOutLegacy.Id);
    }

    [Fact]
    public async Task ListSignInsAsync_IsCappedAtTheNewestFiftySignIns()
    {
        var harness = new Harness(new RefreshSessionSettings { MaxActiveSessionsPerUser = 1000 });
        for (var i = 0; i < 55; i++)
        {
            await harness.SignInAsync(UserId);
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var signIns = await harness.Sut.ListSignInsAsync(UserId, null, CancellationToken.None);

        signIns.Should().HaveCount(50);
        signIns[0].StartedAt.Should().Be(Start.UtcDateTime.AddMinutes(54));
    }

    private static RefreshSession Legacy(UserIdentifierType userId, string token, DateTime createdAt) =>
        new()
        {
            UserId = userId,
            TokenHash = RefreshSession.HashToken(token),
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(7),
        };

    /// <summary>
    /// A real issuer over the in-memory store and a fake clock, minting sequential refresh tokens and
    /// capturing every log line.
    /// </summary>
    private sealed class Harness
    {
        private int _minted;

        public Harness(RefreshSessionSettings? settings = null)
        {
            var tokenService = new Mock<ITokenService>();
            tokenService.Setup(t => t.GenerateRefreshToken()).Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++_minted}"));
            Sut = new AuthSessionIssuer(
                tokenService.Object,
                Store,
                Options.Create(settings ?? new RefreshSessionSettings()),
                Clock,
                new CapturingLogger(Logs));
        }

        public FakeTimeProvider Clock { get; } = new(Start);

        public InMemoryRefreshSessionStore Store { get; } = new();

        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public AuthSessionIssuer Sut { get; }

        /// <summary>The session id the last minted access token was stamped with (its sid).</summary>
        public Guid? LastSessionId { get; private set; }

        public async Task<string> SignInAsync(UserIdentifierType userId)
        {
            var issued = await Sut.IssueAsync(userId, Mint, IpAddress, UserAgent, CancellationToken.None);
            return issued.Value.RefreshToken;
        }

        public async Task<string> RotateAsync(UserIdentifierType userId, string refreshToken)
        {
            var rotated = await Sut.RotateAsync(userId, refreshToken, Mint, IpAddress, UserAgent, CancellationToken.None);
            rotated.IsSuccess.Should().BeTrue();
            return rotated.Value.RefreshToken;
        }

        private string Mint(Guid sessionId)
        {
            LastSessionId = sessionId;
            return "access";
        }
    }

    private sealed class CapturingLogger(List<(LogLevel Level, string Message)> sink) : ILogger<AuthSessionIssuer>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Add((logLevel, formatter(state, exception)));
    }
}
