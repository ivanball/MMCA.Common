using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Services;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Services;

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
    public async Task DeleteAsync_UnscopedOverload_StillDeletesWithoutAnOwnershipLookup()
    {
        var result = await _sut.DeleteAsync(InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Verify(
            x => x.DeleteInstallationAsync(InstallationId, It.IsAny<CancellationToken>()),
            Times.Once);
        _hubClient.Verify(
            x => x.GetInstallationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the unscoped overload is for server-initiated cleanup where the owner is already established");
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
}
