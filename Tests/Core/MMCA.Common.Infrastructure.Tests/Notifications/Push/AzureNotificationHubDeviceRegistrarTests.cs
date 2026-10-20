using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Notifications.Push;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Notifications.Push;

/// <summary>
/// Tests for the Azure Notification Hubs device registrar (ADR-044), focused on the ownership
/// check: installation ids are client-supplied, so the user-scoped delete verifies the
/// <c>user:{id}</c> tag that the upsert stamps before it removes anything.
/// </summary>
public sealed class AzureNotificationHubDeviceRegistrarTests
{
    private const string InstallationId = "3f0c2f9c1e8b4b6c9d3a5e7f0a1b2c3d";
    private const UserIdentifierType Owner = 42;
    private const UserIdentifierType Stranger = 99;

    private readonly Mock<INotificationHubClient> _hubClient = new();
    private readonly AzureNotificationHubDeviceRegistrar _sut;

    public AzureNotificationHubDeviceRegistrarTests() =>
        _sut = new AzureNotificationHubDeviceRegistrar(
            _hubClient.Object,
            NullLogger<AzureNotificationHubDeviceRegistrar>.Instance);

    /// <summary>
    /// The SDK's messaging exceptions have no public constructors, so a thrown instance has to be
    /// materialized without running one.
    /// </summary>
    private static T CreateSdkException<T>()
        where T : Exception =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private void SetupInstallation(params string[] tags) =>
        _hubClient
            .Setup(x => x.GetInstallationAsync(InstallationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Installation
            {
                InstallationId = InstallationId,
                Platform = NotificationPlatform.FcmV1,
                PushChannel = "fcm-registration-token",
                Tags = [.. tags],
            });

    [Fact]
    public async Task DeleteAsync_WhenTheCallerOwnsTheInstallation_Deletes()
    {
        SetupInstallation("user:42");

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.DeleteInstallationAsync(InstallationId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheInstallationBelongsToAnotherUser_DoesNotDelete()
    {
        SetupInstallation("user:42");

        var result = await _sut.DeleteAsync(Stranger, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(
            "not-yours answers exactly like unknown-id, so the endpoint is not an existence oracle");
        _hubClient.Verify(
            x => x.DeleteInstallationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "another user's device must survive a delete aimed at its installation id");
    }

    [Fact]
    public async Task DeleteAsync_WhenTheOwnerTagIsAPrefixOfAnother_DoesNotDelete()
    {
        // "user:420" must not satisfy an ownership check for user 42: the comparison is whole-tag
        // and ordinal, not a prefix match.
        SetupInstallation("user:420");

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.DeleteInstallationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheInstallationHasNoOwnerTag_DoesNotDelete()
    {
        SetupInstallation();

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.DeleteInstallationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheInstallationIsUnknown_SucceedsIdempotently()
    {
        _hubClient
            .Setup(x => x.GetInstallationAsync(InstallationId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(CreateSdkException<MessagingEntityNotFoundException>());

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.DeleteInstallationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheHubFails_ReturnsFailure()
    {
        _hubClient
            .Setup(x => x.GetInstallationAsync(InstallationId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(CreateSdkException<MessagingException>());

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "PushDevice.DeleteFailed");
    }

    [Fact]
    public async Task UpsertAsync_StampsTheOwnerTagTheDeleteChecksFor()
    {
        Installation? captured = null;
        _hubClient
            .Setup(x => x.CreateOrUpdateInstallationAsync(It.IsAny<Installation>(), It.IsAny<CancellationToken>()))
            .Callback<Installation, CancellationToken>((installation, _) => captured = installation)
            .Returns(Task.CompletedTask);

        var result = await _sut.UpsertAsync(
            Owner,
            new()
            {
                InstallationId = InstallationId,
                Platform = "FCMV1",
                PushChannel = "fcm-registration-token",
            },
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Tags.Should().Contain("user:42", "the delete's ownership check reads this tag back");
    }

    // L176: the upsert is a full overwrite, so it must apply the same ownership check as the delete.
    // A caller without the device's push channel cannot take the installation over; the answer is a
    // distinct conflict (installation ids are random client GUIDs, so it reveals nothing useful) and
    // the client recovers by minting a fresh id.
    [Fact]
    public async Task UpsertAsync_WhenTheInstallationBelongsToAnotherUser_ReturnsConflictWithoutOverwriting()
    {
        SetupInstallation("user:42");

        var result = await _sut.UpsertAsync(
            Stranger,
            new()
            {
                InstallationId = InstallationId,
                Platform = "FCMV1",
                PushChannel = "attacker-registration-token",
            },
            TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue("the caller must learn the id is taken so it can mint a new one");
        result.Errors.Should().ContainSingle(e =>
            e.Code == "PushDevice.InstallationConflict" && e.Type == ErrorType.Conflict);
        _hubClient.Verify(
            x => x.CreateOrUpdateInstallationAsync(It.IsAny<Installation>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "re-tagging another user's installation would redirect their pushes to the caller");
    }

    // L176 review (R1): the installation id is per DEVICE (the MAUI client persists it in device
    // preferences and never rotates it), so a second user signing in on the same phone presents the
    // same id AND the same push channel. Holding the channel is possession of the device, so the
    // installation is handed over and re-tagged; the previous owner's pushes stop reaching it.
    [Fact]
    public async Task UpsertAsync_WhenTheSameDeviceChangesHands_RetagsTheInstallationToTheCaller()
    {
        SetupInstallation("user:42");

        var result = await _sut.UpsertAsync(
            Stranger,
            new()
            {
                InstallationId = InstallationId,
                Platform = "FCMV1",
                PushChannel = "fcm-registration-token",
            },
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.CreateOrUpdateInstallationAsync(
                It.Is<Installation>(i => i.Tags.Contains("user:99") && !i.Tags.Contains("user:42")),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the device now belongs to the signed-in user, and the previous owner's pushes must stop");
    }

    [Fact]
    public async Task UpsertAsync_WhenTheCallerAlreadyOwnsTheInstallation_RefreshesIt()
    {
        SetupInstallation("user:42");

        var result = await _sut.UpsertAsync(
            Owner,
            new()
            {
                InstallationId = InstallationId,
                Platform = "FCMV1",
                PushChannel = "rotated-registration-token",
            },
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.CreateOrUpdateInstallationAsync(
                It.Is<Installation>(i => i.PushChannel == "rotated-registration-token"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
