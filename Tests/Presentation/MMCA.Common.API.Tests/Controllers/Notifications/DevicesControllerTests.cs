using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers.Notifications;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Notifications;

/// <summary>
/// Tests for <see cref="DevicesController"/> (ADR-044): the authenticated device-installation
/// endpoints stamp ownership server-side from the current user, pass through registrar
/// failures as Problem Details, and answer 204 on success (PUT upsert and idempotent DELETE).
/// </summary>
public sealed class DevicesControllerTests
{
    private readonly Mock<IPushDeviceRegistrar> _registrar = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();

    private static DeviceInstallationRequest CreateRequest() => new()
    {
        InstallationId = "3f0c2f9c1e8b4b6c9d3a5e7f0a1b2c3d",
        Platform = DeviceInstallationRequest.FcmV1Platform,
        PushChannel = "fcm-registration-token",
    };

    private DevicesController CreateController(UserIdentifierType? userId = 42)
    {
        _currentUserService.Setup(x => x.UserId).Returns(userId);
        return new(_registrar.Object, _currentUserService.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task UpsertAsync_StampsOwnershipFromCurrentUser()
    {
        _registrar
            .Setup(x => x.UpsertAsync(42, It.IsAny<DeviceInstallationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var controller = CreateController();

        var result = await controller.UpsertAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
        _registrar.Verify(
            x => x.UpsertAsync(42, It.Is<DeviceInstallationRequest>(r => r.PushChannel == "fcm-registration-token"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertAsync_WithoutAuthenticatedUser_IsUnauthorized()
    {
        var controller = CreateController(userId: null);

        var result = await controller.UpsertAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _registrar.Verify(
            x => x.UpsertAsync(It.IsAny<UserIdentifierType>(), It.IsAny<DeviceInstallationRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpsertAsync_WhenRegistrarRejects_ReturnsProblemDetails()
    {
        _registrar
            .Setup(x => x.UpsertAsync(42, It.IsAny<DeviceInstallationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation("PushDevice.UnsupportedPlatform", "Bad platform.")));
        var controller = CreateController();

        var result = await controller.UpsertAsync(CreateRequest(), TestContext.Current.CancellationToken);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsNoContent()
    {
        _registrar
            .Setup(x => x.DeleteAsync("abc123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var controller = CreateController();

        var result = await controller.DeleteAsync("abc123", TestContext.Current.CancellationToken);

        result.Should().BeOfType<NoContentResult>();
    }
}
