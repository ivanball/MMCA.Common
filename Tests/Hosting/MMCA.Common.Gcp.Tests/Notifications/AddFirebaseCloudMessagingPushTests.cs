using AwesomeAssertions;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Gcp.Notifications;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Gcp.Tests.Notifications;

/// <summary>
/// The FCM push registration: a no-op unless <c>NativePush:Enabled</c> is true and the
/// <c>NativePush:Fcm</c> section names the project, the collection and the topic; once complete it
/// swaps both inert core defaults (and satisfies the core startup tripwire), and registering it twice
/// is harmless.
/// </summary>
public sealed class AddFirebaseCloudMessagingPushTests
{
    private static Dictionary<string, string?> Complete() => new()
    {
        ["NativePush:Enabled"] = "true",
        ["NativePush:Fcm:ProjectId"] = "demo-project",
        ["NativePush:Fcm:InstallationsCollection"] = "pushInstallations",
        ["NativePush:Fcm:BroadcastTopic"] = "broadcast",
    };

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>
    /// A Firestore database on a placeholder endpoint without credentials: building it opens no
    /// connection, so the services resolve on a machine without Application Default Credentials.
    /// </summary>
    private static FirestoreDb OfflineFirestore() =>
        new FirestoreDbBuilder
        {
            ProjectId = "demo-project",
            Endpoint = "localhost:1",
            ChannelCredentials = ChannelCredentials.Insecure,
        }.Build();

    private static ServiceCollection ServicesWithOfflineFirestore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(OfflineFirestore());
        return services;
    }

    [Theory]
    [InlineData("NativePush:Enabled", null)]
    [InlineData("NativePush:Enabled", "false")]
    [InlineData("NativePush:Enabled", "not-a-bool")]
    [InlineData("NativePush:Fcm:ProjectId", null)]
    [InlineData("NativePush:Fcm:ProjectId", " ")]
    [InlineData("NativePush:Fcm:InstallationsCollection", null)]
    [InlineData("NativePush:Fcm:BroadcastTopic", null)]
    [InlineData("NativePush:Fcm:BroadcastTopic", "")]
    public void AddFirebaseCloudMessagingPush_DisabledOrIncomplete_IsANoOp(string key, string? value)
    {
        var settings = Complete();
        settings[key] = value;
        var services = new ServiceCollection();

        services.AddFirebaseCloudMessagingPush(ConfigurationWith(settings));

        services.Should().NotContain(d => d.ServiceType == typeof(INativePushSender));
        services.Should().NotContain(d => d.ServiceType == typeof(IPushDeviceRegistrar));
        services.Should().NotContain(d => d.ServiceType == typeof(FirestoreDb));
        services.Should().NotContain(d => d.ServiceType == typeof(IFcmMessagingClient));
    }

    [Fact]
    public void AddFirebaseCloudMessagingPush_Complete_SwapsBothCoreDefaults()
    {
        var settings = Complete();
        settings["ConnectionStrings:SqliteConnectionString"] = "Data Source=:memory:";
        var configuration = ConfigurationWith(settings);
        var services = ServicesWithOfflineFirestore();
        services.AddInfrastructure(configuration);

        services.AddFirebaseCloudMessagingPush(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<INativePushSender>().Should().BeOfType<FcmNativePushSender>();
        scope.ServiceProvider.GetRequiredService<IPushDeviceRegistrar>().Should().BeOfType<FcmPushDeviceRegistrar>();
        provider.GetRequiredService<IFcmMessagingClient>().Should().BeOfType<FirebaseMessagingClient>();
        provider.GetRequiredService<IFcmInstallationRegistry>().Should().BeOfType<FirestoreInstallationRegistry>();
        var bound = provider.GetRequiredService<IOptions<FcmNativePushSettings>>().Value;
        bound.ProjectId.Should().Be("demo-project");
        bound.InstallationsCollection.Should().Be("pushInstallations");
        bound.BroadcastTopic.Should().Be("broadcast");
    }

    [Fact]
    public void AddFirebaseCloudMessagingPush_UsesAFirestoreDbTheHostRegisteredFirst()
    {
        var hostFirestore = OfflineFirestore();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(hostFirestore);

        services.AddFirebaseCloudMessagingPush(ConfigurationWith(Complete()));

        services.Should().ContainSingle(d => d.ServiceType == typeof(FirestoreDb));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<FirestoreDb>().Should().BeSameAs(hostFirestore);
    }

    [Fact]
    public void AddFirebaseCloudMessagingPush_RegisteredTwice_IsHarmless()
    {
        var services = ServicesWithOfflineFirestore();
        var configuration = ConfigurationWith(Complete());

        services.AddFirebaseCloudMessagingPush(configuration);
        services.AddFirebaseCloudMessagingPush(configuration);

        services.Should().ContainSingle(d => d.ServiceType == typeof(FirestoreDb));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IFcmMessagingClient));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IFcmInstallationRegistry));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<INativePushSender>().Should().BeOfType<FcmNativePushSender>();
        scope.ServiceProvider.GetRequiredService<IPushDeviceRegistrar>().Should().BeOfType<FcmPushDeviceRegistrar>();
    }

    [Theory]
    [InlineData("broad cast")]
    [InlineData("news/today")]
    [InlineData("/topics/")]
    public void AddFirebaseCloudMessagingPush_AnInvalidTopicName_ThrowsNamingTheKey(string topic)
    {
        var settings = Complete();
        settings["NativePush:Fcm:BroadcastTopic"] = topic;

        var act = () => new ServiceCollection().AddFirebaseCloudMessagingPush(ConfigurationWith(settings));

        act.Should().Throw<InvalidOperationException>().WithMessage("*NativePush:Fcm:BroadcastTopic*");
    }

    [Theory]
    [InlineData("broadcast")]
    [InlineData("/topics/all-users_v2.~%")]
    public void AddFirebaseCloudMessagingPush_AValidTopicName_Registers(string topic)
    {
        var settings = Complete();
        settings["NativePush:Fcm:BroadcastTopic"] = topic;
        var services = new ServiceCollection();

        services.AddFirebaseCloudMessagingPush(ConfigurationWith(settings));

        services.Should().Contain(d => d.ServiceType == typeof(INativePushSender));
    }

    [Fact]
    public void FirestoreBuilder_TargetsTheConfiguredProject_AndIgnoresTheEmulatorVariable()
    {
        var builder = FcmPushClientFactory.BuildFirestoreBuilder(new FcmNativePushSettings { ProjectId = "demo-project" });

        builder.ProjectId.Should().Be("demo-project");
        builder.EmulatorDetection.Should().Be(EmulatorDetection.None, "the endpoint is decided by configuration alone, like the Cloud Storage adapter");
        builder.Endpoint.Should().BeNull();
    }

    [Fact]
    public void MessagingClients_EachOwnAUniquelyNamedApp_NeverTheDefaultOne()
    {
        using var first = FcmPushClientFactory.CreateMessaging(new FcmNativePushSettings { ProjectId = "demo-project" });
        using var second = FcmPushClientFactory.CreateMessaging(new FcmNativePushSettings { ProjectId = "demo-project" });

        first.AppName.Should().StartWith("MMCA.Common.Gcp.Fcm.").And.NotBe(second.AppName);
        first.AppName.Should().NotBe("[DEFAULT]");
    }

    [Fact]
    public void CoreTripwire_FailsWithoutTheAdapter_AndIsSatisfiedWithIt()
    {
        var settings = Complete();
        settings["ConnectionStrings:SqliteConnectionString"] = "Data Source=:memory:";
        var configuration = ConfigurationWith(settings);

        using var withoutAdapter = BuildProvider(configuration, registerAdapter: false);
        using var withAdapter = BuildProvider(configuration, registerAdapter: true);

        var unregistered = () => withoutAdapter.GetRequiredService<IStartupValidator>().Validate();
        var registered = () => withAdapter.GetRequiredService<IStartupValidator>().Validate();

        unregistered.Should().Throw<OptionsValidationException>().WithMessage("*AddFirebaseCloudMessagingPush(configuration)*");
        registered.Should().NotThrow();
        using var scope = withAdapter.CreateScope();
        scope.ServiceProvider.GetRequiredService<INativePushSender>().Should().NotBeOfType<NullNativePushSender>();
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration, bool registerAdapter)
    {
        var services = ServicesWithOfflineFirestore();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration);
        if (registerAdapter)
        {
            services.AddFirebaseCloudMessagingPush(configuration);
        }

        return services.BuildServiceProvider();
    }
}
