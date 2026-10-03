using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Controllers.Legal;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Legal;

/// <summary>
/// Pins the Terms of Service acceptance endpoints: GET answers the caller's standing re-derived
/// against the configured version (and never blocks on a host that configures none), POST records
/// only the configured current version and refuses anything else before the app's service is
/// reached, and both actions sit behind authentication.
/// </summary>
public sealed class LegalAcceptanceControllerBaseTests
{
    private const string CurrentVersion = "2026-10-01";
    private const UserIdentifierType CallerId = 7;

    private readonly Mock<ILegalAcceptanceService> _serviceMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();

    public LegalAcceptanceControllerBaseTests() =>
        _currentUserServiceMock.Setup(s => s.UserId).Returns(CallerId);

    // ── GET ──
    [Fact]
    public async Task GetLegalAcceptanceAsync_AsksTheServiceForTheCallerAtTheConfiguredVersionAndNormalizesTheAnswer()
    {
        var acceptedOn = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        _serviceMock
            .Setup(s => s.GetForCurrentUserAsync(CallerId, CurrentVersion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new LegalAcceptanceDTO
            {
                CurrentVersion = "not-the-configured-one",
                AcceptedVersion = "2026-01-01",
                AcceptedOn = acceptedOn,
                IsCurrent = true,
            }));
        var sut = CreateController(" " + CurrentVersion + " ");

        IActionResult result = await sut.GetLegalAcceptanceAsync();

        var standing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<LegalAcceptanceDTO>().Subject;
        standing.CurrentVersion.Should().Be(CurrentVersion);
        standing.AcceptedVersion.Should().Be("2026-01-01");
        standing.AcceptedOn.Should().Be(acceptedOn);
        standing.IsCurrent.Should().BeFalse("the consumer's IsCurrent is re-derived, never trusted");
        _serviceMock.Verify(
            s => s.GetForCurrentUserAsync(CallerId, CurrentVersion, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetLegalAcceptanceAsync_WithNoVersionConfigured_ReportsCurrentWithoutAskingTheService()
    {
        var sut = CreateController(currentTermsVersion: null);

        IActionResult result = await sut.GetLegalAcceptanceAsync();

        var standing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<LegalAcceptanceDTO>().Subject;
        standing.IsCurrent.Should().BeTrue("a host that never opted in must not block anyone");
        standing.CurrentVersion.Should().BeNull();
        _serviceMock.Verify(
            s => s.GetForCurrentUserAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── POST ──
    [Theory]
    [InlineData("2026-01-01")]
    [InlineData("")]
    public async Task AcceptLegalTermsAsync_WithAVersionOtherThanTheCurrentOne_IsRefusedWithoutCallingTheService(string supplied)
    {
        var sut = CreateController(CurrentVersion);

        IActionResult result = await sut.AcceptLegalTermsAsync(new AcceptLegalTermsRequest(supplied));

        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problemDetails.Extensions["errors"])
            .Should().Contain(LegalAcceptanceErrorCodes.VersionNotCurrent);
        _serviceMock.Verify(
            s => s.AcceptForCurrentUserAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcceptLegalTermsAsync_WithNoVersionConfigured_IsRefusedWithoutCallingTheService()
    {
        var sut = CreateController(currentTermsVersion: null);

        IActionResult result = await sut.AcceptLegalTermsAsync(new AcceptLegalTermsRequest(CurrentVersion));

        var problemDetails = result.Should().BeAssignableTo<ObjectResult>().Which.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problemDetails.Extensions["errors"])
            .Should().Contain(LegalAcceptanceErrorCodes.VersionNotCurrent);
        _serviceMock.Verify(
            s => s.AcceptForCurrentUserAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AcceptLegalTermsAsync_WithThePaddedCurrentVersion_RecordsTheConfiguredString()
    {
        _serviceMock
            .Setup(s => s.AcceptForCurrentUserAsync(CallerId, CurrentVersion, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(LegalAcceptanceDTO.Evaluate(CurrentVersion, CurrentVersion, DateTime.UtcNow)));
        var sut = CreateController(CurrentVersion);

        IActionResult result = await sut.AcceptLegalTermsAsync(new AcceptLegalTermsRequest(" " + CurrentVersion + " "));

        var standing = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<LegalAcceptanceDTO>().Subject;
        standing.IsCurrent.Should().BeTrue();
        _serviceMock.Verify(
            s => s.AcceptForCurrentUserAsync(CallerId, CurrentVersion, It.IsAny<CancellationToken>()),
            Times.Once,
            "the stored version is the configured string, never the client's padded copy");
    }

    // ── Authorization ──
    [Fact]
    public void Controller_RequiresAnAuthenticatedCaller() =>
        typeof(LegalAcceptanceControllerBase)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull(because: "a standing is per-user, and an anonymous caller has none");

    [Theory]
    [InlineData(nameof(LegalAcceptanceControllerBase.GetLegalAcceptanceAsync))]
    [InlineData(nameof(LegalAcceptanceControllerBase.AcceptLegalTermsAsync))]
    public void Action_DoesNotOptOutOfAuthorization(string actionName) =>
        typeof(LegalAcceptanceControllerBase)
            .GetMethod(actionName)!
            .GetCustomAttribute<AllowAnonymousAttribute>()
            .Should().BeNull(because: "an [AllowAnonymous] on the action would override the controller's [Authorize]");

    private TestLegalAcceptanceController CreateController(string? currentTermsVersion) =>
        new(
            _serviceMock.Object,
            _currentUserServiceMock.Object,
            Options.Create(new LegalAcceptanceOptions { CurrentTermsVersion = currentTermsVersion }))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
}

/// <summary>The thin subclass an adopting app writes: route only, nothing else.</summary>
public sealed class TestLegalAcceptanceController(
    ILegalAcceptanceService service,
    ICurrentUserService currentUserService,
    IOptions<LegalAcceptanceOptions> options)
    : LegalAcceptanceControllerBase(service, currentUserService, options);
