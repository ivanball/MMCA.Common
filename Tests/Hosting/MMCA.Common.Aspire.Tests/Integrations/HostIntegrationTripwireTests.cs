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
    public static TheoryData<string, string, string> Integrations => new()
    {
        { "APPLICATIONINSIGHTS_CONNECTION_STRING", HostIntegrationNames.AzureMonitorExporter, "AddAzureMonitorExporter()" },
        { "KeyVault:Uri", HostIntegrationNames.AzureKeyVaultConfiguration, "AddAzureKeyVaultConfiguration()" },
        { "DataProtection:BlobStorageUri", HostIntegrationNames.AzureDataProtection, "AddAzureDataProtection()" },
    };

    private static ValidateOptionsResult Validate(Dictionary<string, string?> settings, params string[] registered)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var tripwire = new HostIntegrationTripwire(configuration, registered.Select(name => new HostIntegrationMarker(name)));
        return tripwire.Validate(Options.DefaultName, new HostIntegrationTripwireOptions());
    }

    [Theory]
    [MemberData(nameof(Integrations))]
    public void ConfiguredWithoutTheAdapterCall_FailsNamingTheCall(string key, string integration, string call)
    {
        _ = integration;

        var result = Validate(new() { [key] = "https://example.invalid/" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(key).And.Contain(call).And.Contain("MMCA.Common.Azure");
    }

    [Theory]
    [MemberData(nameof(Integrations))]
    public void ConfiguredWithTheAdapterCall_Succeeds(string key, string integration, string call)
    {
        _ = call;

        Validate(new() { [key] = "https://example.invalid/" }, integration).Succeeded.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Integrations))]
    public void BlankOrAbsent_StaysSilent(string key, string integration, string call)
    {
        _ = integration;
        _ = call;

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
