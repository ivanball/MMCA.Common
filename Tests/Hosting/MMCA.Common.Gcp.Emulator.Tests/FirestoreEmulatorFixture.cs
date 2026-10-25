using Google.Cloud.Firestore;
using Grpc.Core;
using Testcontainers.Firestore;

[assembly: AssemblyFixture(typeof(MMCA.Common.Gcp.Emulator.Tests.FirestoreEmulatorFixture))]

namespace MMCA.Common.Gcp.Emulator.Tests;

/// <summary>
/// One Firestore emulator container for the whole tier, started once per assembly, behind the FCM
/// installation registry. The image is pinned (the module default, 446.0.1, is no longer published), so an
/// emulator change cannot turn into a red CI run nobody caused. Tests isolate by collection name.
/// </summary>
public sealed class FirestoreEmulatorFixture : IAsyncLifetime
{
    /// <summary>The project id every test uses; the emulator accepts any.</summary>
    public const string ProjectId = "demo-mmca-fcm";

    private readonly FirestoreContainer _container =
        new FirestoreBuilder("gcr.io/google.com/cloudsdktool/google-cloud-cli:588.0.0-emulators").Build();

    /// <summary>
    /// A Firestore database on the emulator over a plain channel. The adapter ignores
    /// <c>FIRESTORE_EMULATOR_HOST</c> by design (the endpoint is configuration), so the tier registers
    /// this database before <c>AddFirebaseCloudMessagingPush</c>, which then uses it as is.
    /// </summary>
    /// <returns>The database.</returns>
    public FirestoreDb CreateFirestoreDb() =>
        new FirestoreDbBuilder
        {
            ProjectId = ProjectId,
            Endpoint = new Uri(_container.GetEmulatorEndpoint()).Authority,
            ChannelCredentials = ChannelCredentials.Insecure,
        }.Build();

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
