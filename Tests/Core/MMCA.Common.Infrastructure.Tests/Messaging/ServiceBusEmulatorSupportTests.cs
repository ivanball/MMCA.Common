using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Messaging;

/// <summary>
/// Unit tests for the Azure Service Bus emulator branch: the detection that decides whether it runs
/// at all, and the management-plane connection string it derives. The live round-trip against a real
/// emulator container is a consumer-side integration tier (ADC's ServiceBusEmulator suite); what has
/// to be pinned here is that production cannot be diverted into this branch by accident and that the
/// admin client is built against the right endpoint with the right key.
/// </summary>
public sealed class ServiceBusEmulatorSupportTests
{
    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost:32771;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    [Fact]
    public void IsEmulatorConnectionString_RecognizesTheEmulatorMarker() =>
        ServiceBusEmulatorSupport.IsEmulatorConnectionString(EmulatorConnectionString).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Endpoint=sb://adc-prod.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=redacted")]
    // The whole safety argument for the emulator branch is that a production connection string never
    // carries the marker, so the branch cannot be entered by a misconfigured environment name or a
    // stray flag.
    public void IsEmulatorConnectionString_LeavesARealNamespaceOnTheProductionPath(string? connectionString) =>
        ServiceBusEmulatorSupport.IsEmulatorConnectionString(connectionString).Should().BeFalse();

    [Fact]
    public void IsEmulatorConnectionString_IgnoresCasing() =>
        ServiceBusEmulatorSupport
            .IsEmulatorConnectionString("Endpoint=sb://localhost:5672;usedevelopmentemulator=TRUE;")
            .Should().BeTrue();

    [Fact]
    public void BuildAdminConnectionString_SwapsTheEndpointAndKeepsEverythingElse()
    {
        // The two planes authenticate against the same emulator namespace, so the key name and value
        // are carried across verbatim: a hand-written second string is one typo away from an admin
        // client that cannot provision anything.
        string admin = ServiceBusEmulatorSupport.BuildAdminConnectionString(
            EmulatorConnectionString, "http://localhost:33012");

        admin.Should().Be(
            "Endpoint=sb://localhost:33012;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
    }

    [Fact]
    public void BuildAdminConnectionString_KeepsTheEmulatorMarker_SoTheAdminClientStaysOnCleartext()
    {
        string admin = ServiceBusEmulatorSupport.BuildAdminConnectionString(
            EmulatorConnectionString, "http://127.0.0.1:5300");

        admin.Should().Contain(ServiceBusEmulatorSupport.EmulatorMarker);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:5300")]
    [InlineData("sb://localhost:5300")]
    public void BuildAdminConnectionString_FailsLoudly_WhenTheAdminEndpointIsMissingOrNotAnHttpUrl(string? adminEndpoint)
    {
        // A bus with no management client cannot provision the topology it needs to run at all, so
        // this has to be a registration failure rather than a mystery at the first publish.
        Action act = () => ServiceBusEmulatorSupport.BuildAdminConnectionString(EmulatorConnectionString, adminEndpoint);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MessageBus:EmulatorAdminEndpoint*");
    }

    [Fact]
    public void MessageBusSettings_BindsTheAdminEndpointFromConfiguration()
    {
        MessageBusSettings settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MessageBus:Provider"] = "AzureServiceBus",
                ["MessageBus:ConnectionString"] = EmulatorConnectionString,
                ["MessageBus:EmulatorAdminEndpoint"] = "http://localhost:33012",
            })
            .Build()
            .GetSection(MessageBusSettings.SectionName)
            .Get<MessageBusSettings>()!;

        settings.Provider.Should().Be(MessageBusProvider.AzureServiceBus);
        settings.EmulatorAdminEndpoint.Should().Be("http://localhost:33012");
    }

    [Fact]
    public void MessageBusSettings_AdminEndpointIsUnsetByDefault() =>
        new MessageBusSettings().EmulatorAdminEndpoint.Should().BeNull(
            because: "a real Azure Service Bus namespace serves both planes on one endpoint and needs nothing here");

    [Fact]
    public void EmulatorEntityQuota_SitsAtTheEmulatorsOneHourCeiling() =>
        ServiceBusEmulatorSupport.EmulatorEntityQuota.Should().Be(TimeSpan.FromHours(1),
            because: "the emulator rejects any entity whose TTL or auto-delete-on-idle exceeds one hour, "
                + "and MassTransit v8 defaults to 366 days TTL and 427 days auto-delete");
}
