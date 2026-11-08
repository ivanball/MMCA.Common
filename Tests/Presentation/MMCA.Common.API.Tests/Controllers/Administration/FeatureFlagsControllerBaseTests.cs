using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.FeatureFlags;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// The thin feature-flag administration controller: the permission that is its whole protection,
/// no route of its own, and the Result-to-ActionResult mapping of its three endpoints.
/// </summary>
public sealed class FeatureFlagsControllerBaseTests
{
    private const string Flag = "Sales.PaymentVerification";

    private static readonly FeatureFlagStatusResponse Status = new(
        Flag, FeatureFlagLifetime.Permanent, "payments", null, FeatureFlagValueKind.On, [], null, FeatureFlagValueKind.On);

    private readonly Mock<IFeatureFlagAdministrationService> _administration = new();

    [Fact]
    public void TheBaseIsGatedOnFeaturesManage_AndCarriesNoRoute()
    {
        var type = typeof(FeatureFlagsControllerBase);

        type.GetCustomAttributes<HasPermissionAttribute>().Select(attribute => attribute.Permission)
            .Should().Equal(AdministrationPermissions.ManageFeatures);
        AdministrationPermissions.ManageFeatures.Should().Be("features:manage");
        type.GetCustomAttributes<RouteAttribute>(inherit: true).Should().BeEmpty("the derived controller supplies the route");
        type.IsAbstract.Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_ReturnsTheDeclaredFlags()
    {
        _administration.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<FeatureFlagStatusResponse>>([Status]));

        var result = await CreateController().GetAllAsync();

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(new[] { Status });
    }

    [Fact]
    public async Task Set_PassesTheRequestThrough_AndReturnsTheFlag()
    {
        var request = new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, "Provider outage");
        _administration.Setup(x => x.SetOverrideAsync(Flag, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Status));

        var result = await CreateController().SetOverrideAsync(Flag, request);

        result.Result.Should().BeOfType<OkObjectResult>();
        _administration.Verify(x => x.SetOverrideAsync(Flag, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Set_AnUndeclaredFlag_IsA404()
    {
        var request = new SetFeatureFlagOverrideRequest(FeatureFlagOverrideMode.Off, "Provider outage");
        _administration.Setup(x => x.SetOverrideAsync("Nope.Flag", request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<FeatureFlagStatusResponse>(
                Error.NotFoundError("FeatureFlag.Undeclared", "No such flag.")));

        var result = await CreateController().SetOverrideAsync("Nope.Flag", request);

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Clear_ReturnsNoContent()
    {
        _administration.Setup(x => x.ClearOverrideAsync(Flag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var result = await CreateController().ClearOverrideAsync(Flag);

        result.Should().BeOfType<NoContentResult>();
    }

    private TestFeatureFlagsController CreateController() =>
        new(_administration.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private sealed class TestFeatureFlagsController(IFeatureFlagAdministrationService administration)
        : FeatureFlagsControllerBase(administration);
}
