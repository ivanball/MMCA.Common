using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Sessions;

/// <summary>
/// Recording the device a native client reports for the caller's session
/// (<see cref="AuthSessionIssuer.RecordDeviceAsync"/>): only the user's own live session can be
/// described, and what is recorded shows on the device list and the sign-in history and survives
/// rotation.
/// </summary>
public sealed class AuthSessionIssuerDeviceTests
{
    private const UserIdentifierType UserId = 1;
    private const UserIdentifierType OtherUserId = 2;

    private static readonly DateTimeOffset Start = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    private static readonly DeviceMetadata Pixel = new("install-1", "Ana's phone", "Pixel 8", "Google", "Android", "Phone", "Physical");

    [Fact]
    public async Task RecordDeviceAsync_OnTheUsersLiveSession_IsShownOnTheDeviceListAndTheSignInHistory()
    {
        var harness = new Harness();
        await harness.SignInAsync(UserId);
        var sessionId = harness.LastSessionId!.Value;

        var result = await harness.Sut.RecordDeviceAsync(UserId, sessionId, Pixel, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var device = (await harness.Sut.ListActiveAsync(UserId, sessionId, CancellationToken.None))
            .Should().ContainSingle().Subject.Device;
        device.Should().Be(Pixel);
        (await harness.Sut.ListSignInsAsync(UserId, sessionId, CancellationToken.None))
            .Should().ContainSingle().Subject.Device.Should().Be(Pixel);
    }

    [Fact]
    public async Task RecordDeviceAsync_ThenARefresh_TheRotatedSessionStillNamesTheDevice()
    {
        var harness = new Harness();
        var token = await harness.SignInAsync(UserId);
        await harness.Sut.RecordDeviceAsync(UserId, harness.LastSessionId!.Value, Pixel, CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(20));

        await harness.RotateAsync(UserId, token);

        var live = await harness.Sut.ListActiveAsync(UserId, harness.LastSessionId, CancellationToken.None);
        live.Should().ContainSingle().Which.Device.Should().Be(Pixel, "a refresh must not forget the device the client reported once");
    }

    [Fact]
    public async Task ListActiveAsync_ForASessionWithNoReportedDevice_HasNoDevice()
    {
        var harness = new Harness();
        await harness.SignInAsync(UserId);

        var live = await harness.Sut.ListActiveAsync(UserId, null, CancellationToken.None);

        live.Should().ContainSingle().Which.Device.Should().BeNull("a browser session reports nothing and is described from its user-agent");
    }

    [Fact]
    public async Task RecordDeviceAsync_OnAnotherUsersSession_IsNotFound_AndRecordsNothing()
    {
        var harness = new Harness();
        await harness.SignInAsync(OtherUserId);
        var othersSession = harness.LastSessionId!.Value;

        var result = await harness.Sut.RecordDeviceAsync(UserId, othersSession, Pixel, CancellationToken.None);

        AssertSessionNotFound(result);
        harness.Store.Sessions.Should().ContainSingle().Which.DeviceModel.Should().BeNull();
    }

    [Fact]
    public async Task RecordDeviceAsync_OnARevokedSession_IsNotFound_AndRecordsNothing()
    {
        var harness = new Harness();
        await harness.SignInAsync(UserId);
        var sessionId = harness.LastSessionId!.Value;
        (await harness.Sut.RevokeSessionAsync(UserId, sessionId, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await harness.Sut.RecordDeviceAsync(UserId, sessionId, Pixel, CancellationToken.None);

        AssertSessionNotFound(result);
        harness.Store.Sessions.Should().ContainSingle().Which.DeviceModel.Should().BeNull();
    }

    [Fact]
    public async Task RecordDeviceAsync_OnAnExpiredSession_IsNotFound_AndRecordsNothing()
    {
        var harness = new Harness();
        await harness.SignInAsync(UserId);
        var sessionId = harness.LastSessionId!.Value;
        harness.Clock.Advance(TimeSpan.FromDays(8));

        var result = await harness.Sut.RecordDeviceAsync(UserId, sessionId, Pixel, CancellationToken.None);

        AssertSessionNotFound(result);
        harness.Store.Sessions.Should().ContainSingle().Which.DeviceModel.Should().BeNull();
    }

    [Fact]
    public async Task RecordDeviceAsync_OnASessionThatNeverExisted_IsNotFound()
    {
        var harness = new Harness();
        await harness.SignInAsync(UserId);

        var result = await harness.Sut.RecordDeviceAsync(UserId, Guid.NewGuid(), Pixel, CancellationToken.None);

        AssertSessionNotFound(result);
    }

    private static void AssertSessionNotFound(Result result)
    {
        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Type.Should().Be(ErrorType.NotFound);
        error.Code.Should().Be("Auth.SessionNotFound");
    }

    private sealed class Harness
    {
        private int _minted;

        public Harness()
        {
            var tokenService = new Mock<ITokenService>();
            tokenService.Setup(t => t.GenerateRefreshToken()).Returns(() => string.Create(CultureInfo.InvariantCulture, $"refresh-{++_minted}"));
            Sut = new AuthSessionIssuer(tokenService.Object, Store, Options.Create(new RefreshSessionSettings()), Clock);
        }

        public FakeTimeProvider Clock { get; } = new(Start);

        public InMemoryRefreshSessionStore Store { get; } = new();

        public AuthSessionIssuer Sut { get; }

        /// <summary>The session id the last minted access token was stamped with (its sid).</summary>
        public Guid? LastSessionId { get; private set; }

        public async Task<string> SignInAsync(UserIdentifierType userId)
        {
            var issued = await Sut.IssueAsync(userId, Mint, "203.0.113.7", "Firefox", CancellationToken.None);
            return issued.Value.RefreshToken;
        }

        public async Task<string> RotateAsync(UserIdentifierType userId, string refreshToken)
        {
            var rotated = await Sut.RotateAsync(userId, refreshToken, Mint, "203.0.113.7", "Firefox", CancellationToken.None);
            rotated.IsSuccess.Should().BeTrue();
            return rotated.Value.RefreshToken;
        }

        private string Mint(Guid sessionId)
        {
            LastSessionId = sessionId;
            return "access";
        }
    }
}
