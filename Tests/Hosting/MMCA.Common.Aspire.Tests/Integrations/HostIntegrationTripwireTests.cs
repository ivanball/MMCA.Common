using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Aspire.Tests.Integrations;

/// <summary>
/// The service-defaults startup tripwire: a vendor integration the configuration asks for, with no
/// adapter call registered, fails the host naming the missing call; configured-and-registered, or not
/// configured at all, stays silent.
/// </summary>
public sealed class HostIntegrationTripwireTests
{
    public static TheoryData<string, string, string, string> Integrations => new()
    {
        { "APPLICATIONINSIGHTS_CONNECTION_STRING", HostIntegrationNames.AzureMonitorExporter, "AddAzureMonitorExporter()", "MMCA.Common.Azure" },
        { "KeyVault:Uri", HostIntegrationNames.AzureKeyVaultConfiguration, "AddAzureKeyVaultConfiguration()", "MMCA.Common.Azure" },
        { "DataProtection:BlobStorageUri", HostIntegrationNames.AzureDataProtection, "AddAzureDataProtection()", "MMCA.Common.Azure" },
        { "SecretsManager:NamePrefix", HostIntegrationNames.AwsSecretsManagerConfiguration, "AddAwsSecretsManagerConfiguration()", "MMCA.Common.Aws" },
        { "DataProtection:SsmParameterPrefix", HostIntegrationNames.AwsDataProtection, "AddAwsDataProtection()", "MMCA.Common.Aws" },
        { "GoogleSecretManager:NamePrefix", HostIntegrationNames.GoogleSecretManagerConfiguration, "AddGoogleSecretManagerConfiguration()", "MMCA.Common.Gcp" },
        { "DataProtection:GcsObjectUri", HostIntegrationNames.GoogleCloudDataProtection, "AddGoogleCloudDataProtection()", "MMCA.Common.Gcp" },
    };

    public static TheoryData<string, string, string, string> KeyRingPairs => new()
    {
        { "DataProtection:BlobStorageUri", HostIntegrationNames.AzureDataProtection, "DataProtection:SsmParameterPrefix", HostIntegrationNames.AwsDataProtection },
        { "DataProtection:BlobStorageUri", HostIntegrationNames.AzureDataProtection, "DataProtection:GcsObjectUri", HostIntegrationNames.GoogleCloudDataProtection },
        { "DataProtection:SsmParameterPrefix", HostIntegrationNames.AwsDataProtection, "DataProtection:GcsObjectUri", HostIntegrationNames.GoogleCloudDataProtection },
    };

    private static ValidateOptionsResult Validate(Dictionary<string, string?> settings, params string[] registered)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var tripwire = new HostIntegrationTripwire(configuration, registered.Select(name => new HostIntegrationMarker(name)));
        return tripwire.Validate(Options.DefaultName, new HostIntegrationTripwireOptions());
    }

    [Theory]
    [MemberData(nameof(Integrations))]
    public void ConfiguredWithoutTheAdapterCall_FailsNamingTheCall(string key, string integration, string call, string package)
    {
        _ = integration;

        var result = Validate(new() { [key] = "https://example.invalid/" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(key).And.Contain(call).And.Contain(package);
    }

    [Theory]
    [MemberData(nameof(Integrations))]
    public void ConfiguredWithTheAdapterCall_Succeeds(string key, string integration, string call, string package)
    {
        _ = call;
        _ = package;

        Validate(new() { [key] = "https://example.invalid/" }, integration).Succeeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Integrations))]
    public void BlankOrAbsent_StaysSilent(string key, string integration, string call, string package)
    {
        _ = integration;
        _ = call;
        _ = package;

        Validate(new() { [key] = "  " }).Succeeded.Should().BeTrue("a blank value is how a template leaves the integration off");
        Validate([]).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void EveryMissingIntegration_IsReportedInOneFailure()
    {
        var result = Validate(new()
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000000",
            ["KeyVault:Uri"] = "https://vault.example.invalid/",
            ["DataProtection:BlobStorageUri"] = "https://blob.example.invalid/keys/keys.xml",
        });

        result.Failures.Should().HaveCount(3);
    }

    [Fact]
    public void BothDataProtectionKeyRings_Fail_EvenWithBothAdapterCalls()
    {
        // Two configured key rings is the split-brain the shared key ring exists to prevent: some
        // replicas or services would mint cookies on one ring and others could not read them.
        var result = Validate(
            new()
            {
                ["DataProtection:BlobStorageUri"] = "https://blob.example.invalid/keys/keys.xml",
                ["DataProtection:SsmParameterPrefix"] = "/adc/prod/dataprotection",
            },
            HostIntegrationNames.AzureDataProtection,
            HostIntegrationNames.AwsDataProtection);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("DataProtection:BlobStorageUri").And.Contain("DataProtection:SsmParameterPrefix");
    }

    [Theory]
    [MemberData(nameof(KeyRingPairs))]
    public void AnyTwoDataProtectionKeyRings_Fail_EvenWithBothAdapterCalls(string firstKey, string firstIntegration, string secondKey, string secondIntegration)
    {
        var result = Validate(
            new()
            {
                [firstKey] = "gs://keys/keys.xml",
                [secondKey] = "gs://keys/keys.xml",
            },
            firstIntegration,
            secondIntegration);

        result.Failed.Should().BeTrue();
        // The message lists every key-ring key, so the pair that is actually set is asserted as named.
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain($"({firstKey} and {secondKey})");
    }

    [Fact]
    public void AllThreeDataProtectionKeyRings_FailOnce_NamingEach()
    {
        var result = Validate(
            new()
            {
                ["DataProtection:BlobStorageUri"] = "https://blob.example.invalid/keys/keys.xml",
                ["DataProtection:SsmParameterPrefix"] = "/adc/prod/dataprotection",
                ["DataProtection:GcsObjectUri"] = "gs://mmca-keys/keys.xml",
            },
            HostIntegrationNames.AzureDataProtection,
            HostIntegrationNames.AwsDataProtection,
            HostIntegrationNames.GoogleCloudDataProtection);

        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("(DataProtection:BlobStorageUri and DataProtection:SsmParameterPrefix and DataProtection:GcsObjectUri)");
    }

    [Fact]
    public void GoogleSecretManagerProjectIdAlone_StaysSilent() =>
        // The prefix is what declares "this host reads its settings from Secret Manager"; a project id
        // alone names a location, not a set of secrets, and other Google settings may share it.
        Validate(new() { ["GoogleSecretManager:ProjectId"] = "mmca-prod" })
            .Succeeded.Should().BeTrue();

    [Fact]
    public void OneGoogleKeyRing_WithBlankOthers_StaysSilent() =>
        Validate(
            new()
            {
                ["DataProtection:BlobStorageUri"] = "  ",
                ["DataProtection:SsmParameterPrefix"] = string.Empty,
                ["DataProtection:GcsObjectUri"] = "gs://mmca-keys/keys.xml",
            },
            HostIntegrationNames.GoogleCloudDataProtection)
            .Succeeded.Should().BeTrue("blank placeholders for the other clouds' key rings are not extra key rings");

    [Fact]
    public void OneDataProtectionKeyRing_WithABlankOther_StaysSilent() =>
        Validate(
            new()
            {
                ["DataProtection:BlobStorageUri"] = "  ",
                ["DataProtection:SsmParameterPrefix"] = "/adc/prod/dataprotection",
            },
            HostIntegrationNames.AwsDataProtection)
            .Succeeded.Should().BeTrue("a blank placeholder for the other cloud's key ring is not a second key ring");

    [Fact]
    public void AddServiceDefaults_ValidatesOnStart_AndFailsTheHost()
    {
        // The end-to-end wiring: AddServiceDefaults registers the tripwire with ValidateOnStart, so the
        // startup validator (what Host.StartAsync runs before any hosted service) throws.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "https://vault.example.invalid/",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
        });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>().WithMessage("*KeyVault:Uri*AddAzureKeyVaultConfiguration()*");
    }

    [Fact]
    public void ConfigureOpenTelemetry_LeavesTheServiceDefaultsReceipt()
    {
        // The receipt AddAzureMonitorExporter() checks for, so a vendor exporter can refuse to be
        // registered ahead of the filter processors.
        var builder = Host.CreateApplicationBuilder();

        builder.ConfigureOpenTelemetry();

        builder.Services.Should().Contain(descriptor =>
            descriptor.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)descriptor.ImplementationInstance).Name == HostIntegrationNames.ServiceDefaultsTelemetry);
    }
}
