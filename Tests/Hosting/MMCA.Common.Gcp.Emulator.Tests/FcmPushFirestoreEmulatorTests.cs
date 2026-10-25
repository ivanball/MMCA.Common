using AwesomeAssertions;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Gcp.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Gcp.Emulator.Tests;

/// <summary>
/// The shipped FCM push pair, registered through <c>AddFirebaseCloudMessagingPush</c> exactly as a
/// host does, against the Firestore emulator. The unit tier pins what the registrar does with each
/// answer of the registry; only a real Firestore settles the answers themselves: that the
/// transaction refuses another device's installation and hands over the same device, that the
/// owner-scoped and token-scoped deletes leave a changed document alone, that two racing claims
/// cannot both win, and that the <c>UserId</c> <c>WhereIn</c> query (30 values at a time) finds every
/// user's installations. FCM has no emulator, so the FCM client is a recording fake.
/// </summary>
/// <param name="emulator">The shared Firestore emulator.</param>
public sealed class FcmPushFirestoreEmulatorTests(FirestoreEmulatorFixture emulator) : IAsyncLifetime
{
    private const UserIdentifierType Owner = 41;
    private const UserIdentifierType Stranger = 77;

    private readonly string _collection = $"installations-{Guid.NewGuid():N}";
    private readonly RecordingFcmMessagingClient _messaging = new();
    private ServiceProvider _host = null!;
    private FirestoreDb _firestore = null!;

    private IPushDeviceRegistrar Registrar => _host.GetRequiredService<IPushDeviceRegistrar>();

    private INativePushSender Sender => _host.GetRequiredService<INativePushSender>();

    public ValueTask InitializeAsync()
    {
        _firestore = emulator.CreateFirestoreDb();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(_firestore);
        services.AddSingleton<IFcmMessagingClient>(_messaging);
        services.AddFirebaseCloudMessagingPush(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NativePush:Enabled"] = "true",
            ["NativePush:Fcm:ProjectId"] = FirestoreEmulatorFixture.ProjectId,
            ["NativePush:Fcm:InstallationsCollection"] = _collection,
            ["NativePush:Fcm:BroadcastTopic"] = "broadcast",
        }).Build());
        _host = services.BuildServiceProvider();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static DeviceInstallationRequest Request(string installationId, string pushChannel) => new()
    {
        InstallationId = installationId,
        Platform = DeviceInstallationRequest.FcmV1Platform,
        PushChannel = pushChannel,
    };

    private Task<DocumentSnapshot> DocumentAsync(string installationId, CancellationToken ct) =>
        _firestore.Collection(_collection).Document(installationId).GetSnapshotAsync(ct);

    private static string Key(int user) => user.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Upsert_NewInstallation_WritesTheOwnedDocument()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct);

        result.IsSuccess.Should().BeTrue();
        var document = await DocumentAsync("device-1", ct);
        document.Exists.Should().BeTrue();
        document.GetValue<string>("UserId").Should().Be("41");
        document.GetValue<string>("Platform").Should().Be("fcmv1");
        document.GetValue<string>("PushChannel").Should().Be("token-a");
        document.GetValue<Timestamp>("UpdatedAt").ToDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        _messaging.Subscribed.Should().Equal("token-a");
    }

    [Fact]
    public async Task Upsert_SameUserWithANewToken_UpdatesTheTokenAndMovesTheSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct)).IsSuccess.Should().BeTrue();

        var result = await Registrar.UpsertAsync(Owner, Request("device-1", "token-rotated"), ct);

        result.IsSuccess.Should().BeTrue();
        (await DocumentAsync("device-1", ct)).GetValue<string>("PushChannel").Should().Be("token-rotated");
        _messaging.Subscribed.Should().Equal("token-a", "token-rotated");
        _messaging.Unsubscribed.Should().Equal("token-a");
    }

    [Fact]
    public async Task Upsert_AnotherDevicesInstallationId_IsAConflict_AndLeavesTheDocumentUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct)).IsSuccess.Should().BeTrue();
        var before = await DocumentAsync("device-1", ct);

        var result = await Registrar.UpsertAsync(Stranger, Request("device-1", "token-b"), ct);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.InstallationConflict");
        result.Errors[0].Type.Should().Be(ErrorType.Conflict);
        var after = await DocumentAsync("device-1", ct);
        after.GetValue<string>("UserId").Should().Be("41");
        after.GetValue<string>("PushChannel").Should().Be("token-a");
        after.UpdateTime.Should().Be(before.UpdateTime, "a refused claim writes nothing");
        _messaging.Subscribed.Should().Equal("token-a");
    }

    [Fact]
    public async Task Upsert_TheSameDeviceChangingHands_IsHandedOver()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "shared-token"), ct)).IsSuccess.Should().BeTrue();

        var result = await Registrar.UpsertAsync(Stranger, Request("device-1", "shared-token"), ct);

        result.IsSuccess.Should().BeTrue();
        var document = await DocumentAsync("device-1", ct);
        document.GetValue<string>("UserId").Should().Be("77");
        document.GetValue<string>("PushChannel").Should().Be("shared-token");
        _messaging.Unsubscribed.Should().BeEmpty("the token did not change");
    }

    [Fact]
    public async Task Upsert_TwoDevicesRacingForOneId_ExactlyOneWins()
    {
        var ct = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(
            Registrar.UpsertAsync(Owner, Request("device-1", "token-owner"), ct),
            Registrar.UpsertAsync(Stranger, Request("device-1", "token-stranger"), ct));

        results.Count(r => r.IsSuccess).Should().Be(1);
        results.Single(r => r.IsFailure).Errors.Should().ContainSingle().Which.Code.Should().Be("PushDevice.InstallationConflict");
        var winner = results[0].IsSuccess ? ("41", "token-owner") : ("77", "token-stranger");
        var document = await DocumentAsync("device-1", ct);
        (document.GetValue<string>("UserId"), document.GetValue<string>("PushChannel")).Should().Be(winner);
    }

    [Fact]
    public async Task Delete_IsOwnerScoped_ByAnotherUserOrForAnUnknownIdItDeletesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-1", "token-a"), ct)).IsSuccess.Should().BeTrue();

        (await Registrar.DeleteAsync(Stranger, "device-1", ct)).IsSuccess.Should().BeTrue();
        (await Registrar.DeleteAsync(Owner, "no-such-device", ct)).IsSuccess.Should().BeTrue();

        (await DocumentAsync("device-1", ct)).Exists.Should().BeTrue();
        _messaging.Unsubscribed.Should().BeEmpty();

        (await Registrar.DeleteAsync(Owner, "device-1", ct)).IsSuccess.Should().BeTrue();

        (await DocumentAsync("device-1", ct)).Exists.Should().BeFalse();
        _messaging.Unsubscribed.Should().Equal("token-a");
    }

    [Fact]
    public async Task SendToUsers_FindsEveryUsersInstallations_AcrossMoreUsersThanOneQueryTakes()
    {
        var ct = TestContext.Current.CancellationToken;
        var users = Enumerable.Range(1, FirestoreInstallationRegistry.MaxUsersPerQuery + 1).ToList();
        foreach (var user in users)
        {
            (await Registrar.UpsertAsync(user, Request("device-" + Key(user), "token-" + Key(user)), ct)).IsSuccess.Should().BeTrue();
        }

        (await Registrar.UpsertAsync(Stranger, Request("device-stranger", "token-stranger"), ct)).IsSuccess.Should().BeTrue();
        (await Registrar.UpsertAsync(1, Request("device-1b", "token-1b"), ct)).IsSuccess.Should().BeTrue();

        await Sender.SendToUsersAsync(users, "Hello", "World", cancellationToken: ct);

        _messaging.SentTokens.Should().BeEquivalentTo([.. users.Select(u => "token-" + Key(u)), "token-1b"], "user 77 was not targeted");
    }

    [Fact]
    public async Task SendToUsers_PrunesADeadToken_ButNotADocumentThatMovedToANewToken()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Registrar.UpsertAsync(Owner, Request("device-dead", "token-dead"), ct)).IsSuccess.Should().BeTrue();
        (await Registrar.UpsertAsync(Owner, Request("device-live", "token-live"), ct)).IsSuccess.Should().BeTrue();
        _messaging.DeadTokens.Add("token-dead");

        await Sender.SendToUsersAsync([Owner], "Hello", "World", cancellationToken: ct);

        (await DocumentAsync("device-dead", ct)).Exists.Should().BeFalse();
        (await DocumentAsync("device-live", ct)).Exists.Should().BeTrue();

        // The token-scoped delete is conditional: a document refreshed onto a new token survives a
        // cleanup aimed at its old one.
        var registry = _host.GetRequiredService<IFcmInstallationRegistry>();
        (await registry.DeleteIfPushChannelAsync("device-live", "token-from-before", ct)).Should().BeFalse();
        (await DocumentAsync("device-live", ct)).Exists.Should().BeTrue();
    }
}
