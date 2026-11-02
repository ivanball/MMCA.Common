using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Sessions;

/// <summary>
/// Lost-response recovery on <see cref="AuthSessionIssuer.RotateAsync"/>
/// (<see cref="RefreshSessionSettings.LostResponseRecoveryMinutes"/>, off by default). A mobile client
/// whose refresh succeeded on the server while the response was lost still holds the old pair and
/// presents it minutes later. Inside the window, and only while the successor was never presented,
/// that presentation rotates the successor instead of revoking the user's whole family (BR-206); a
/// successor that was used, has expired, or a presentation past the window keeps the BR-206 answer.
/// </summary>
public sealed class AuthSessionIssuerLostResponseRecoveryTests
{
    private const UserIdentifierType UserId = 1;
    private const string OldToken = "old-refresh";
    private const string SuccessorToken = "successor-refresh";
    private const string MintedToken = "minted-refresh";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 17, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Now = FixedNow.UtcDateTime;

    private static readonly RefreshSessionSettings RecoveryOn = new() { LostResponseRecoveryMinutes = 120 };

    [Fact]
    public void LostResponseRecoveryMinutes_DefaultsToOff() =>
        new RefreshSessionSettings().LostResponseRecoveryMinutes.Should().Be(0, "framework behavior stays opt-in");

    [Fact]
    public async Task RotateAsync_WhenTheSuccessorWasNeverPresentedInsideTheWindow_RotatesTheSuccessor_AndKeepsTheFamily()
    {
        var store = new InMemoryRefreshSessionStore();
        var (old, successor) = SeedRotatedPair(store, rotatedMinutesAgo: 10);
        var otherDevice = store.SeedLive(UserId, "laptop-refresh", Now.AddHours(-3), Now.AddDays(6));

        Result<AuthenticationResponse> result = await RotateAsync(store, RecoveryOn);

        result.IsSuccess.Should().BeTrue("the client lost the rotation's response, which is not theft");
        result.Value!.RefreshToken.Should().Be(MintedToken);
        successor.ReasonRevoked.Should().Be(RefreshSession.ReasonRotated, "the never-presented successor is rotated in place of the old row");
        successor.ReplacedByTokenHash.Should().Be(RefreshSession.HashToken(MintedToken));
        old.ReasonRevoked.Should().Be(RefreshSession.ReasonRotated, "the presented row keeps its original revocation");
        otherDevice.IsRevoked.Should().BeFalse("no family revocation runs for a recovered response");
        store.Sessions.Should().NotContain(s => s.ReasonRevoked == RefreshSession.ReasonReuseDetected);
    }

    [Fact]
    public async Task RotateAsync_WhenTheSuccessorWasAlreadyPresented_RevokesTheFamily()
    {
        var store = new InMemoryRefreshSessionStore();
        var (_, successor) = SeedRotatedPair(store, rotatedMinutesAgo: 10);
        successor.Revoke(Now.AddMinutes(-5), RefreshSession.ReasonRotated, RefreshSession.HashToken("grandchild-refresh"));
        var grandchild = store.SeedLive(UserId, "grandchild-refresh", Now.AddMinutes(-5), Now.AddDays(6));

        Result<AuthenticationResponse> result = await RotateAsync(store, RecoveryOn);

        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken" && e.Type == ErrorType.Unauthorized);
        grandchild.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected, "two holders advancing one chain is the BR-206 theft signal");
    }

    [Fact]
    public async Task RotateAsync_WhenTheRotationIsOlderThanTheWindow_RevokesTheFamily()
    {
        var store = new InMemoryRefreshSessionStore();
        var (_, successor) = SeedRotatedPair(store, rotatedMinutesAgo: 121);

        Result<AuthenticationResponse> result = await RotateAsync(store, RecoveryOn);

        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        successor.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    [Fact]
    public async Task RotateAsync_WithRecoveryOff_RevokesTheFamily_AsBefore()
    {
        var store = new InMemoryRefreshSessionStore();
        var (_, successor) = SeedRotatedPair(store, rotatedMinutesAgo: 10);

        Result<AuthenticationResponse> result = await RotateAsync(store, new RefreshSessionSettings());

        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        successor.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    [Fact]
    public async Task RotateAsync_WhenTheSuccessorHasExpired_RevokesTheFamily()
    {
        var store = new InMemoryRefreshSessionStore();
        var old = store.SeedLive(UserId, OldToken, Now.AddDays(-8), Now.AddDays(-1));
        old.Revoke(Now.AddMinutes(-10), RefreshSession.ReasonRotated, RefreshSession.HashToken(SuccessorToken));
        var expiredSuccessor = store.SeedLive(UserId, SuccessorToken, Now.AddDays(-7), Now.AddMinutes(-1));
        var otherDevice = store.SeedLive(UserId, "laptop-refresh", Now.AddHours(-3), Now.AddDays(6));

        Result<AuthenticationResponse> result = await RotateAsync(store, RecoveryOn);

        result.Errors.Should().ContainSingle(e => e.Code == "Auth.InvalidRefreshToken");
        expiredSuccessor.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
        otherDevice.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected);
    }

    [Fact]
    public async Task RotateAsync_WhenASiblingJustRecoveredTheSameStaleToken_ReturnsConflict_AndKeepsTheFamily()
    {
        var store = new InMemoryRefreshSessionStore();
        SeedRotatedPair(store, rotatedMinutesAgo: 10);
        var otherDevice = store.SeedLive(UserId, "laptop-refresh", Now.AddHours(-3), Now.AddDays(6));

        Result<AuthenticationResponse> first = await RotateAsync(store, RecoveryOn);
        Result<AuthenticationResponse> second = await RotateAsync(store, RecoveryOn, minted: "second-minted");

        first.IsSuccess.Should().BeTrue();
        second.IsFailure.Should().BeTrue();
        second.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Conflict, "the sibling's recovery a moment ago is a rotation race");
        otherDevice.IsRevoked.Should().BeFalse();
        store.Sessions.Should().NotContain(s => s.ReasonRevoked == RefreshSession.ReasonReuseDetected);
    }

    [Fact]
    public async Task RotateAsync_InsideTheReuseGrace_StillAnswersConflict_BeforeAnyRecovery()
    {
        var store = new InMemoryRefreshSessionStore();
        var (_, successor) = SeedRotatedPair(store, rotatedMinutesAgo: 0, rotatedSecondsAgo: 3);

        Result<AuthenticationResponse> result = await RotateAsync(store, RecoveryOn);

        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Conflict);
        successor.IsRevoked.Should().BeFalse("inside the grace the 409 answers first and nothing is rotated");
    }

    private static (RefreshSession Old, RefreshSession Successor) SeedRotatedPair(
        InMemoryRefreshSessionStore store,
        int rotatedMinutesAgo,
        int rotatedSecondsAgo = 0)
    {
        var rotatedAt = Now.AddMinutes(-rotatedMinutesAgo).AddSeconds(-rotatedSecondsAgo);
        var old = store.SeedLive(UserId, OldToken, rotatedAt.AddMinutes(-15), Now.AddDays(6));
        old.Revoke(rotatedAt, RefreshSession.ReasonRotated, RefreshSession.HashToken(SuccessorToken));
        var successor = store.SeedLive(UserId, SuccessorToken, rotatedAt, Now.AddDays(6));
        return (old, successor);
    }

    private static Task<Result<AuthenticationResponse>> RotateAsync(
        InMemoryRefreshSessionStore store,
        RefreshSessionSettings settings,
        string minted = MintedToken)
    {
        var tokenService = new Mock<ITokenService>();
        tokenService.Setup(t => t.GenerateRefreshToken()).Returns(minted);
        var sut = new AuthSessionIssuer(
            tokenService.Object,
            store,
            Options.Create(settings),
            new FixedTimeProvider(FixedNow));

        return sut.RotateAsync(UserId, OldToken, _ => "access-1", ipAddress: null, userAgent: null, TestContext.Current.CancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
