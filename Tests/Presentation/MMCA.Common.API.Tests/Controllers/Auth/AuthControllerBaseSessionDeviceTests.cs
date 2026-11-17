using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Auth;

/// <summary>
/// <c>PUT auth/sessions/current/device</c> (<see cref="AuthControllerBase.RecordCurrentSessionDeviceAsync"/>)
/// over the real <see cref="AuthSessionIssuer"/>: the device lands on the session the caller's own
/// <c>sid</c> names, and anything else (no <c>sid</c>, another account's session, a revoked or expired
/// one) is a 404 that writes nothing.
/// </summary>
public sealed class AuthControllerBaseSessionDeviceTests
{
    private const int UserId = 42;
    private const int OtherUserId = 43;

    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly DeviceMetadata Pixel = new("install-1", "Ana's phone", "Pixel 8", "Google", "Android", "Phone", "Physical");

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly Mock<IRefreshSessionStore> _store = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    public AuthControllerBaseSessionDeviceTests() => _currentUser.Setup(x => x.UserId).Returns(UserId);

    [Fact]
    public async Task RecordCurrentSessionDevice_OnTheCallersLiveSession_RecordsItAndReturnsNoContent()
    {
        var session = SessionOf(UserId);

        var result = await PutAsync(session.Id);

        result.Should().BeOfType<NoContentResult>();
        session.DeviceName.Should().Be("Ana's phone");
        session.DeviceModel.Should().Be("Pixel 8");
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordCurrentSessionDevice_WithNoSessionClaim_IsNotFound_AndTouchesNoSession()
    {
        var result = await PutAsync(sessionClaim: null);

        AssertNotFound(result);
        _store.Verify(s => s.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordCurrentSessionDevice_WhenTheSidNamesAnotherAccountsSession_IsNotFound_AndRecordsNothing()
    {
        var othersSession = SessionOf(OtherUserId);

        var result = await PutAsync(othersSession.Id);

        AssertNotFound(result);
        othersSession.DeviceModel.Should().BeNull();
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordCurrentSessionDevice_OnARevokedSession_IsNotFound_AndRecordsNothing()
    {
        var session = SessionOf(UserId);
        session.Revoke(Start.UtcDateTime, RefreshSession.ReasonSignedOut);

        var result = await PutAsync(session.Id);

        AssertNotFound(result);
        session.DeviceModel.Should().BeNull();
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordCurrentSessionDevice_OnAnExpiredSession_IsNotFound_AndRecordsNothing()
    {
        var session = SessionOf(UserId);
        _clock.Advance(TimeSpan.FromDays(8));

        var result = await PutAsync(session.Id);

        AssertNotFound(result);
        session.DeviceModel.Should().BeNull();
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordCurrentSessionDevice_WithNoAuthenticatedUser_IsUnauthorized()
    {
        _currentUser.Setup(x => x.UserId).Returns((int?)null);

        var result = await PutAsync(Guid.NewGuid());

        result.Should().BeOfType<UnauthorizedResult>();
    }

    /// <summary>
    /// A session row owned by <paramref name="owner"/>, served by the store the way the real one does:
    /// the lookup is scoped to the owner, so asking for it as anyone else finds nothing.
    /// </summary>
    private RefreshSession SessionOf(int owner)
    {
        var session = RefreshSession.Create(owner, string.Create(CultureInfo.InvariantCulture, $"token-{owner}"), Start.UtcDateTime, Start.UtcDateTime.AddDays(7)).Value!;
        _store
            .Setup(s => s.FindByIdAsync(session.Id, owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        return session;
    }

    private Task<ActionResult> PutAsync(Guid? sessionClaim)
    {
        var issuer = new AuthSessionIssuer(
            Mock.Of<ITokenService>(),
            _store.Object,
            Options.Create(new RefreshSessionSettings()),
            _clock);

        var controller = new TestAuthController(Mock.Of<IAuthenticationService>(), _currentUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        if (sessionClaim is { } sid)
        {
            controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(AuthClaimTypes.SessionId, sid.ToString("D"))],
                "Test"));
        }

        return controller.RecordCurrentSessionDeviceAsync(Pixel, issuer, CancellationToken.None);
    }

    private static void AssertNotFound(ActionResult result)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }
}
