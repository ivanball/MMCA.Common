using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.API.Controllers.Privacy;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.Users;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Privacy;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Privacy;

/// <summary>
/// Pins the shipped data-subject export endpoint: the download shape a subject actually receives,
/// the Problem Details failure path, and the two attributes that are the whole of its security
/// posture. The attributes are asserted directly because nothing else fails when one is dropped:
/// the endpoint simply becomes anonymous, or becomes reachable in a host that never enabled it.
/// </summary>
public sealed class DataExportControllerBaseTests
{
    private static readonly DateTimeOffset GeneratedOn = new(2026, 8, 13, 17, 42, 9, TimeSpan.Zero);

    private readonly Mock<IQueryHandler<TestExportQuery, Result<UserDataExportDTO>>> _handlerMock = new();
    private readonly Mock<ICurrentUserService> _currentUserServiceMock = new();

    [Fact]
    public async Task ExportAsync_WhenUnauthenticated_ReturnsUnauthorizedProblemDetails()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns((UserIdentifierType?)null);
        TestDataExportController sut = CreateController();

        IActionResult result = await sut.ExportAsync(userId: 7);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
    }

    [Fact]
    public async Task ExportAsync_WhenHandlerFails_ReturnsProblemDetailsCarryingTheErrors()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(2);
        _handlerMock
            .Setup(h => h.HandleAsync(It.IsAny<TestExportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<UserDataExportDTO>(Error.Forbidden(
                code: "User.ExportForbidden",
                message: "You can only export your own account data.",
                source: "ExportUserDataHandler",
                target: "UserId")));
        TestDataExportController sut = CreateController();

        IActionResult result = await sut.ExportAsync(userId: 7);

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Status.Should().Be(StatusCodes.Status403Forbidden);
        problemDetails.Extensions.Should().ContainKey("errors");
    }

    [Fact]
    public async Task ExportAsync_OnSuccess_ReturnsTheDownloadWithTheDatedFileName()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(7);
        ArrangeExport(CreateExport());
        TestDataExportController sut = CreateController();

        IActionResult result = await sut.ExportAsync(userId: 7);

        var fileResult = result.Should().BeOfType<FileContentResult>().Subject;
        fileResult.ContentType.Should().Be("application/json");
        fileResult.FileDownloadName.Should().Be("user-data-7-20260813.json",
            "the file name carries the package's own generated-on date, formatted invariantly");
    }

    [Fact]
    public async Task ExportAsync_OnSuccess_WritesABodyThatRoundTripsThePackage()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(7);
        ArrangeExport(CreateExport());
        TestDataExportController sut = CreateController();

        IActionResult result = await sut.ExportAsync(userId: 7);

        var fileResult = (FileContentResult)result;
        var roundTripped = JsonSerializer.Deserialize<UserDataExportDTO>(
            fileResult.FileContents, JsonSerializerOptions.Web);

        roundTripped.Should().NotBeNull();
        roundTripped!.FormatVersion.Should().Be("1.0");
        roundTripped.UserId.Should().Be(7);
        roundTripped.GeneratedOn.Should().Be(GeneratedOn);
        roundTripped.Sections.Should().HaveCount(2);
        roundTripped.Sections[0].SectionName.Should().Be("Engagement");
        roundTripped.Sections[0].Available.Should().BeTrue();
        roundTripped.Sections[1].SectionName.Should().Be("Sales");
        roundTripped.Sections[1].Available.Should().BeFalse();
        roundTripped.Sections[1].UnavailableReason.Should().Be("Temporarily unreachable.");
    }

    [Fact]
    public async Task ExportAsync_BuildsTheQueryFromTheRouteAndTheAuthenticatedCaller()
    {
        _currentUserServiceMock.Setup(s => s.UserId).Returns(2);
        _currentUserServiceMock.Setup(s => s.Role).Returns("Organizer");
        TestExportQuery? capturedQuery = null;
        _handlerMock
            .Setup(h => h.HandleAsync(It.IsAny<TestExportQuery>(), It.IsAny<CancellationToken>()))
            .Callback<TestExportQuery, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(Result.Success(CreateExport()));
        TestDataExportController sut = CreateController();

        await sut.ExportAsync(userId: 7);

        capturedQuery.Should().NotBeNull();
        capturedQuery!.UserId.Should().Be(7);
        capturedQuery.CurrentUserId.Should().Be(2);
        capturedQuery.CurrentUserRole.Should().Be("Organizer");
    }

    [Fact]
    public void Controller_RequiresAnAuthenticatedCaller() =>
        typeof(DataExportControllerBase<TestExportQuery>)
            .GetCustomAttribute<AuthorizeAttribute>()
            .Should().NotBeNull(
                because: "the handler's ownership check is the second line of defence, not the first");

    [Fact]
    public void Controller_IsGatedOnThePrivacyDataExportFlag()
    {
        var featureGate = typeof(DataExportControllerBase<TestExportQuery>)
            .GetCustomAttribute<FeatureGateAttribute>();

        featureGate.Should().NotBeNull(
            because: "the DSAR surface stays off until a host deliberately enables it");
        featureGate!.Features.Should().Equal(PrivacyFeatures.DataExport);
    }

    // The gate is an action filter, so a disabled feature is answered by the filter rather than by
    // the controller. Driven directly here so "off" is a verified 404 (the framework's standard
    // disabled-feature response: the endpoint does not exist as far as the caller is concerned)
    // rather than an assumption about the attribute's default behaviour.
    [Fact]
    public async Task FeatureGate_WhenTheFlagIsOff_ShortCircuitsWithTheDisabledFeatureResponse()
    {
        var featureGate = typeof(DataExportControllerBase<TestExportQuery>)
            .GetCustomAttribute<FeatureGateAttribute>()!;
        ActionExecutingContext context = CreateActionExecutingContext(featureEnabled: false);

        await featureGate.OnActionExecutionAsync(
            context,
            () => throw new InvalidOperationException("the action must not run when the feature is off"));

        var statusCodeResult = context.Result.Should().BeAssignableTo<StatusCodeResult>().Subject;
        statusCodeResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task FeatureGate_WhenTheFlagIsOn_LetsTheActionRun()
    {
        var featureGate = typeof(DataExportControllerBase<TestExportQuery>)
            .GetCustomAttribute<FeatureGateAttribute>()!;
        ActionExecutingContext context = CreateActionExecutingContext(featureEnabled: true);
        var ran = false;

        await featureGate.OnActionExecutionAsync(
            context,
            () =>
            {
                ran = true;
                return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
            });

        ran.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    private static UserDataExportDTO CreateExport() =>
        new()
        {
            FormatVersion = "1.0",
            GeneratedOn = GeneratedOn,
            UserId = 7,
            Subject = new SubjectSnapshot("jane@example.com", "Attendee"),
            Sections =
            [
                new UserDataExportSectionDTO
                {
                    SectionName = "Engagement",
                    Available = true,
                    Data = new { Bookmarks = 2 },
                },
                new UserDataExportSectionDTO
                {
                    SectionName = "Sales",
                    Available = false,
                    UnavailableReason = "Temporarily unreachable.",
                },
            ],
        };

    private void ArrangeExport(UserDataExportDTO export) =>
        _handlerMock
            .Setup(h => h.HandleAsync(It.IsAny<TestExportQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(export));

    private TestDataExportController CreateController() =>
        new(_handlerMock.Object, _currentUserServiceMock.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

    private static ActionExecutingContext CreateActionExecutingContext(bool featureEnabled)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFeatureManagerSnapshot>(new StubFeatureManager(featureEnabled));

        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor()),
            [],
            new Dictionary<string, object?>(StringComparer.Ordinal),
            controller: new object());
    }

    private sealed record SubjectSnapshot(string Email, string Role);

    private sealed class StubFeatureManager(bool enabled) : IFeatureManagerSnapshot
    {
        public async IAsyncEnumerable<string> GetFeatureNamesAsync()
        {
            yield return PrivacyFeatures.DataExport;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public Task<bool> IsEnabledAsync(string feature) => Task.FromResult(enabled);

        public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) => Task.FromResult(enabled);
    }
}

/// <summary>App-side export query shape, standing in for each app's <c>ExportUserDataQuery</c>.</summary>
public sealed record TestExportQuery(
    UserIdentifierType UserId,
    UserIdentifierType CurrentUserId,
    string? CurrentUserRole) : IUserOwnedRequest;

/// <summary>
/// The thin subclass an adopting app writes: route, query type, nothing else.
/// </summary>
public sealed class TestDataExportController(
    IQueryHandler<TestExportQuery, Result<UserDataExportDTO>> exportHandler,
    ICurrentUserService currentUserService)
    : DataExportControllerBase<TestExportQuery>(exportHandler, currentUserService)
{
    protected override TestExportQuery CreateQuery(
        UserIdentifierType userId,
        UserIdentifierType currentUserId,
        string? currentUserRole) =>
        new(userId, currentUserId, currentUserRole);
}
