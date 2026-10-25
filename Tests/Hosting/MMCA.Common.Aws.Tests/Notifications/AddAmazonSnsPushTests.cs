using Amazon.DynamoDBv2;
using Amazon.SimpleNotificationService;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Aws.Notifications;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Aws.Tests.Notifications;

/// <summary>
/// The SNS push registration: a no-op unless <c>NativePush:Enabled</c> is true and the
/// <c>NativePush:Sns</c> section names the table, the topic and at least one platform application;
/// once complete it swaps both inert core defaults, and the core startup tripwire is satisfied.
/// </summary>
public sealed class AddAmazonSnsPushTests
{
    private static Dictionary<string, string?> Complete() => new()
    {
        ["NativePush:Enabled"] = "true",
        ["NativePush:Sns:FcmPlatformApplicationArn"] = "arn:aws:sns:eu-west-1:000000000000:app/GCM/fcm",
        ["NativePush:Sns:BroadcastTopicArn"] = "arn:aws:sns:eu-west-1:000000000000:broadcast",
        ["NativePush:Sns:InstallationsTable"] = "installations",
        ["NativePush:Sns:Region"] = "eu-west-1",
    };

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string?> Without(string key, Dictionary<string, string?>? settings = null)
    {
        settings ??= Complete();
        settings.Remove(key);
        return settings;
    }

    [Theory]
    [InlineData("NativePush:Enabled", null)]
    [InlineData("NativePush:Enabled", "false")]
    [InlineData("NativePush:Enabled", "not-a-bool")]
    [InlineData("NativePush:Sns:InstallationsTable", null)]
    [InlineData("NativePush:Sns:BroadcastTopicArn", null)]
    [InlineData("NativePush:Sns:BroadcastTopicArn", " ")]
    [InlineData("NativePush:Sns:FcmPlatformApplicationArn", null)]
    public void AddAmazonSnsPush_DisabledOrIncomplete_IsANoOp(string key, string? value)
    {
        var settings = value is null ? Without(key) : new Dictionary<string, string?>(Complete()) { [key] = value };
        var services = new ServiceCollection();

        services.AddAmazonSnsPush(ConfigurationWith(settings));

        services.Should().NotContain(d => d.ServiceType == typeof(INativePushSender));
        services.Should().NotContain(d => d.ServiceType == typeof(IPushDeviceRegistrar));
        services.Should().NotContain(d => d.ServiceType == typeof(IAmazonSimpleNotificationService));
        services.Should().NotContain(d => d.ServiceType == typeof(IAmazonDynamoDB));
    }

    [Theory]
    [InlineData("NativePush:Sns:FcmPlatformApplicationArn")]
    [InlineData("NativePush:Sns:ApnsPlatformApplicationArn")]
    public void AddAmazonSnsPush_Complete_SwapsBothCoreDefaults(string platformKey)
    {
        var settings = Without("NativePush:Sns:FcmPlatformApplicationArn");
        settings[platformKey] = "arn:aws:sns:eu-west-1:000000000000:app/X/app";
        settings["ConnectionStrings:SqliteConnectionString"] = "Data Source=:memory:";
        var configuration = ConfigurationWith(settings);
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure(configuration);

        services.AddAmazonSnsPush(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<INativePushSender>().Should().BeOfType<SnsNativePushSender>();
        scope.ServiceProvider.GetRequiredService<IPushDeviceRegistrar>().Should().BeOfType<SnsPushDeviceRegistrar>();
        provider.GetRequiredService<IAmazonSimpleNotificationService>().Config.RegionEndpoint.SystemName.Should().Be("eu-west-1");
        provider.GetRequiredService<IAmazonDynamoDB>().Config.RegionEndpoint.SystemName.Should().Be("eu-west-1");
        provider.GetRequiredService<IOptions<SnsNativePushSettings>>().Value.InstallationsTable.Should().Be("installations");
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

        unregistered.Should().Throw<OptionsValidationException>().WithMessage("*AddAmazonSnsPush(configuration)*");
        registered.Should().NotThrow();
        using var scope = withAdapter.CreateScope();
        scope.ServiceProvider.GetRequiredService<INativePushSender>().Should().NotBeOfType<NullNativePushSender>();
    }

    [Fact]
    public void AddAmazonSnsPush_WithAServiceUrl_PointsBothClientsAtIt()
    {
        var settings = Complete();
        settings["NativePush:Sns:ServiceUrl"] = "http://localhost:4566/";
        settings["NativePush:Sns:AccessKeyId"] = "test";
        settings["NativePush:Sns:SecretAccessKey"] = "test";
        var services = new ServiceCollection().AddLogging();

        services.AddAmazonSnsPush(ConfigurationWith(settings));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAmazonSimpleNotificationService>().Config.ServiceURL.Should().Be("http://localhost:4566/");
        provider.GetRequiredService<IAmazonDynamoDB>().Config.ServiceURL.Should().Be("http://localhost:4566/");
        provider.GetRequiredService<IAmazonDynamoDB>().Config.AuthenticationRegion.Should().Be("eu-west-1");
    }

    [Theory]
    [InlineData("AKIDEXAMPLE", null)]
    [InlineData(null, "secret")]
    public void AddAmazonSnsPush_HalfAKeyPair_Throws(string? accessKeyId, string? secretAccessKey)
    {
        var settings = Complete();
        settings["NativePush:Sns:AccessKeyId"] = accessKeyId;
        settings["NativePush:Sns:SecretAccessKey"] = secretAccessKey;

        var act = () => new ServiceCollection().AddAmazonSnsPush(ConfigurationWith(settings));

        act.Should().Throw<InvalidOperationException>().WithMessage("*NativePush:Sns:AccessKeyId*NativePush:Sns:SecretAccessKey*");
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration, bool registerAdapter)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration);
        if (registerAdapter)
        {
            services.AddAmazonSnsPush(configuration);
        }

        return services.BuildServiceProvider();
    }
}
