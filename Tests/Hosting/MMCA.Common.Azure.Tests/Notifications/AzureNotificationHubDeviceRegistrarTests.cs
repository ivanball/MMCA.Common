using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Azure.Tests.Notifications;

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

    private readonly FakeNotificationHubRestClient _hubClient = new();
    private readonly AzureNotificationHubDeviceRegistrar _sut;

    public AzureNotificationHubDeviceRegistrarTests() =>
        _sut = new AzureNotificationHubDeviceRegistrar(
            _hubClient,
            NullLogger<AzureNotificationHubDeviceRegistrar>.Instance);

    private void SetupInstallation(params string[] tags) => _hubClient.AddInstallation(InstallationId, tags);

    private static NotificationHubRegistrationPage Page(string? continuationToken, params string[][] registrations) =>
        new([.. registrations.Select(tags => (IReadOnlySet<string>)new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase))], continuationToken);

    [Fact]
    public async Task DeleteAsync_WhenTheCallerOwnsTheInstallation_Deletes()
    {
        SetupInstallation("user:42");

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Deletes.Should().Equal(InstallationId);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheInstallationBelongsToAnotherUser_DoesNotDelete()
    {
        SetupInstallation("user:42");

        var result = await _sut.DeleteAsync(Stranger, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue(
            "not-yours answers exactly like unknown-id, so the endpoint is not an existence oracle");
        _hubClient.Deletes.Should().BeEmpty("another user's device must survive a delete aimed at its installation id");
    }

    [Fact]
    public async Task DeleteAsync_WhenTheOwnerTagIsAPrefixOfAnother_DoesNotDelete()
    {
        // "user:420" must not satisfy an ownership check for user 42: the comparison is whole-tag
        // and ordinal, not a prefix match.
        SetupInstallation("user:420");

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_WhenTheInstallationHasNoOwnerTag_DoesNotDelete()
    {
        SetupInstallation();

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_WhenTheInstallationIsUnknown_SucceedsIdempotently()
    {
        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAsync_WhenTheHubFails_ReturnsFailure()
    {
        _hubClient.GetFailure = FakeNotificationHubRestClient.ServerBusy();

        var result = await _sut.DeleteAsync(Owner, InstallationId, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "PushDevice.DeleteFailed");
    }

    [Fact]
    public async Task DeleteAllForUserAsync_DeletesEveryInstallationFoundUnderTheOwnerTag_AcrossPages()
    {
        const string second = "second-installation";
        _hubClient.Pages[string.Empty] = Page("page-2", ["user:42", $"$InstallationId:{{{InstallationId}}}"]);
        _hubClient.Pages["page-2"] = Page(
            string.Empty,
            ["user:42", $"$installationid:{{{second}}}"],
            ["user:42", $"$InstallationId:{{{InstallationId}}}"]);
        SetupInstallation("user:42");
        _hubClient.AddInstallation(second, "user:42");

        var result = await _sut.DeleteAllForUserAsync(Owner, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.ListedTags.Should().Equal("user:42", "user:42");
        _hubClient.Deletes.Should().BeEquivalentTo(
            [InstallationId, second],
            "an installation seen on two registrations is still deleted once, the second page is read, and the system tag matches case-insensitively as the hub does");
    }

    [Fact]
    public async Task DeleteAllForUserAsync_WhenAnInstallationChangedHandsMeanwhile_LeavesIt()
    {
        _hubClient.Pages[string.Empty] = Page(string.Empty, ["user:42", $"$InstallationId:{{{InstallationId}}}"]);
        SetupInstallation("user:99");

        var result = await _sut.DeleteAllForUserAsync(Owner, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Deletes.Should().BeEmpty(
            "each delete re-checks the owner tag on the installation itself, so a handed-over device survives");
    }

    [Fact]
    public async Task DeleteAllForUserAsync_WithNoRegistrations_SucceedsWithoutDeleting()
    {
        _hubClient.Pages[string.Empty] = Page(null, ["user:42"]);

        var result = await _sut.DeleteAllForUserAsync(Owner, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("a registration with no installation system tag names nothing to delete");
        _hubClient.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAllForUserAsync_WhenListingFails_ReturnsFailure()
    {
        _hubClient.ListFailure = FakeNotificationHubRestClient.ServerBusy();

        var result = await _sut.DeleteAllForUserAsync(Owner, TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue("a failure is returned so the erasure's internal command retries");
        result.Errors.Should().ContainSingle(e => e.Code == "PushDevice.DeleteFailed");
    }

    [Fact]
    public async Task UpsertAsync_StampsTheOwnerTagTheDeleteChecksFor()
    {
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
        var upsert = _hubClient.Upserts.Should().ContainSingle().Subject;
        upsert.Should().Be(new NotificationHubInstallation(InstallationId, NotificationHubPlatform.FcmV1, "fcm-registration-token", upsert.Tags));
        upsert.Tags.Should().Equal(["user:42"], "the delete's ownership check reads this tag back");
    }

    [Theory]
    [InlineData("apns", "Apns")]
    [InlineData("fcmv1", "FcmV1")]
    public async Task UpsertAsync_MapsThePlatformCaseInsensitively(string platform, string expected)
    {
        var result = await _sut.UpsertAsync(
            Owner,
            new() { InstallationId = InstallationId, Platform = platform, PushChannel = "channel" },
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _hubClient.Upserts.Should().ContainSingle().Which.Platform.ToString().Should().Be(expected);
    }

    [Fact]
    public async Task UpsertAsync_AnUnsupportedPlatform_IsAValidationFailureWithoutAHubCall()
    {
        var result = await _sut.UpsertAsync(
            Owner,
            new() { InstallationId = InstallationId, Platform = "wns", PushChannel = "channel" },
            TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == "PushDevice.UnsupportedPlatform" && e.Type == ErrorType.Validation);
        _hubClient.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAsync_WhenTheHubFails_ReturnsFailure()
    {
        _hubClient.UpsertFailure = FakeNotificationHubRestClient.ServerBusy();

        var result = await _sut.UpsertAsync(
            Owner,
            new() { InstallationId = InstallationId, Platform = "FCMV1", PushChannel = "channel" },
            TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == "PushDevice.UpsertFailed");
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
        _hubClient.Upserts.Should().BeEmpty("re-tagging another user's installation would redirect their pushes to the caller");
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
        _hubClient.Upserts.Should().ContainSingle().Which.Tags.Should().Equal(
            ["user:99"],
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
        _hubClient.Upserts.Should().ContainSingle().Which.PushChannel.Should().Be("rotated-registration-token");
    }
}
