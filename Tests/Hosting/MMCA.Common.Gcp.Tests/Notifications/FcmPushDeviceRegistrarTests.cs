using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Gcp.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Gcp.Tests.Notifications;

/// <summary>
/// The FCM device registrar (ADR-044) against a scripted registry. It pins the contract the
/// Notification Hubs and SNS registrars honour: a new installation is written; a re-registration
/// updates the token; another device's installation is a conflict; the same device changing hands
/// is a handover; only FCM tokens are accepted; and a delete is owner-scoped with no existence
/// oracle. The ownership decision itself (the Firestore transaction) is pinned against the
/// emulator in the emulator tier.
/// </summary>
public sealed class FcmPushDeviceRegistrarTests
{
    private const string InstallationId = "3f0c2f9c1e8b4b6c9d3a5e7f0a1b2c3d";
    private const UserIdentifierType Owner = 42;

    private readonly FakeFcmInstallationRegistry _registry = new();

    private FcmPushDeviceRegistrar CreateSut() =>
        new(_registry, NullLogger<FcmPushDeviceRegistrar>.Instance);

    private static DeviceInstallationRequest Request(string pushChannel = "token-new", string platform = "fcmv1") => new()
    {
        InstallationId = InstallationId,
        Platform = platform,
        PushChannel = pushChannel,
    };

    private void AddInstallation(string owner, string pushChannel) =>
        _registry.Installations[InstallationId] = new FcmInstallation(InstallationId, owner, "fcmv1", pushChannel);

    private void ShouldHaveNoDelete()
    {
        _registry.OwnedDeletes.Should().BeEmpty();
        _registry.PushChannelDeletes.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_NewInstallation_WritesTheDocument()
    {
        var result = await CreateSut().UpsertAsync(Owner, Request(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _registry.Claims.Should().Equal(new FcmInstallation(InstallationId, "42", "fcmv1", "token-new"));
    }

    [Fact]
    public async Task UpsertAsync_SameUserReRegistersWithANewToken_UpdatesTheToken()
    {
        _registry.ClaimAnswer = (true, "token-old");

        var result = await CreateSut().UpsertAsync(Owner, Request("token-rotated"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _registry.Claims.Should().ContainSingle().Which.PushChannel.Should().Be("token-rotated");
        ShouldHaveNoDelete();
    }

    [Fact]
    public async Task UpsertAsync_AnotherUsersInstallationWithADifferentChannel_IsAConflict()
    {
        _registry.ClaimAnswer = (false, "their-token");

        var result = await CreateSut().UpsertAsync(Owner, Request("my-token"), TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.InstallationConflict");
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
        ShouldHaveNoDelete();
    }

    [Fact]
    public async Task UpsertAsync_AnotherUsersInstallationWithTheSameChannel_IsHandedOver()
    {
        // The registry claims it for the caller because the caller presented the installation's channel.
        _registry.ClaimAnswer = (true, "shared-device-token");

        var result = await CreateSut().UpsertAsync(Owner, Request("shared-device-token"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _registry.Claims.Should().ContainSingle().Which.UserId.Should().Be("42");
    }

    [Theory]
    [InlineData("apns")]
    [InlineData("APNS")]
    public async Task UpsertAsync_AnApnsToken_FailsValidationNamingFcmTokens(string platform)
    {
        var result = await CreateSut().UpsertAsync(Owner, Request(platform: platform), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.Validation);
        result.Errors[0].Code.Should().Be("PushDevice.UnsupportedPlatform");
        result.Errors[0].Message.Should().Contain("iOS").And.Contain("FCM registration token").And.Contain("fcmv1");
        _registry.Claims.Should().BeEmpty();
    }

    [Theory]
    [InlineData("wns")]
    [InlineData("")]
    [InlineData("gcm")]
    public async Task UpsertAsync_AnUnknownPlatform_FailsValidation(string platform)
    {
        var result = await CreateSut().UpsertAsync(Owner, Request(platform: platform), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.UnsupportedPlatform");
        _registry.Claims.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_FcmV1InAnyCase_IsAcceptedAndStoredNormalized()
    {
        var result = await CreateSut().UpsertAsync(Owner, Request(platform: "FcmV1"), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _registry.Claims.Should().ContainSingle().Which.Platform.Should().Be("fcmv1");
    }

    [Fact]
    public async Task UpsertAsync_RegistryFailure_ReturnsAFailureResult()
    {
        _registry.ClaimException = new RpcException(new Status(StatusCode.Unavailable, "boom"));

        var result = await CreateSut().UpsertAsync(Owner, Request(), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.UpsertFailed");
    }

    [Fact]
    public async Task DeleteAsync_ByTheOwner_Deletes()
    {
        AddInstallation("42", "token");

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _registry.OwnedDeletes.Should().Equal((InstallationId, "42"));
    }

    [Fact]
    public async Task DeleteAsync_ByAnotherUser_SucceedsAndDeletesNothing()
    {
        AddInstallation("99", "token");

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        ShouldHaveNoDelete();
    }

    [Fact]
    public async Task DeleteAsync_UnknownInstallation_SucceedsAndDeletesNothing()
    {
        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        ShouldHaveNoDelete();
    }

    [Fact]
    public async Task DeleteAsync_RegistryFailure_ReturnsAFailureResult()
    {
        AddInstallation("42", "token");
        _registry.DeleteException = new RpcException(new Status(StatusCode.Unavailable, "boom"));

        var result = await CreateSut().DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.DeleteFailed");
    }
}
