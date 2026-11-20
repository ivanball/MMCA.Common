using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Azure.Startup.Tests.Telemetry;

/// <summary>
/// <c>AddAzureMonitorExporter()</c>: refuses to run ahead of the service defaults whose filter
/// processors must precede it, and satisfies the core startup tripwire that fails a host carrying an
/// Application Insights connection string without it. Like the AWS and GCP twins, each of the three
/// Azure startup calls leaves its receipt only when it actually registered something, so a gate key
/// that arrives through a source added after the call still fails the host, naming the call.
/// </summary>
public sealed class AzureMonitorExporterTests
{
    private const string ConnectionString =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";

    [Fact]
    public void BeforeServiceDefaults_Throws()
    {
        var builder = Host.CreateApplicationBuilder();

        var act = () => builder.AddAzureMonitorExporter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*after AddServiceDefaults()*");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AfterServiceDefaults_TheTripwireIsSatisfied(bool connectionStringSet)
    {
        var builder = BuilderWith(new()
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = connectionStringSet ? ConnectionString : string.Empty,
        });
        builder.AddServiceDefaults();

        builder.AddAzureMonitorExporter();

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().NotThrow();
    }

    [Fact]
    public void ConnectionStringWithoutTheExporter_FailsTheHost()
    {
        var builder = BuilderWith(new() { ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ConnectionString });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>().WithMessage("*AddAzureMonitorExporter()*");
    }

    [Fact]
    public void WithAConnectionString_LeavesTheReceipt()
    {
        var builder = BuilderWith(new() { ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ConnectionString });
        builder.AddServiceDefaults();

        builder.AddAzureMonitorExporter();

        builder.Services.Should().Contain(
            d => d.ImplementationInstance is HostIntegrationMarker
                && string.Equals(((HostIntegrationMarker)d.ImplementationInstance).Name, HostIntegrationNames.AzureMonitorExporter, StringComparison.Ordinal),
            because: "a call that registered the exporter leaves the receipt the startup check reads");
    }

    [Fact]
    public void WithoutAConnectionString_LeavesNoReceipt()
    {
        var builder = BuilderWith(new() { ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty });
        builder.AddServiceDefaults();

        builder.AddAzureMonitorExporter();

        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker
                && string.Equals(((HostIntegrationMarker)d.ImplementationInstance).Name, HostIntegrationNames.AzureMonitorExporter, StringComparison.Ordinal),
            because: "a call that added nothing leaves no receipt, so a connection string that appears later still trips the startup check");
    }

    [Theory]
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING", ConnectionString, "AddAzureMonitorExporter()")]
    [InlineData("KeyVault:Uri", "https://vault.example.invalid/", "AddAzureKeyVaultConfiguration()")]
    [InlineData("DataProtection:BlobStorageUri", "https://blob.example.invalid/keys/keys.xml", "AddAzureDataProtection()")]
    public void ACallThatAddedNothing_ThenAGateKeyFromALaterSource_FailsTheHostNamingTheCall(
        string gateKey,
        string gateValue,
        string missingCall)
    {
        // Every call runs while its gate key is still blank, so it registers nothing; the key then
        // arrives through a source added after the call. A call that added nothing leaves no receipt,
        // so the startup check fails instead of passing a host that runs without the integration.
        var builder = BuilderWith([]);
        builder.AddServiceDefaults();
        builder.AddAzureKeyVaultConfiguration();
        builder.AddAzureDataProtection();
        builder.AddAzureMonitorExporter();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [gateKey] = gateValue });

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>().WithMessage($"*{missingCall}*");
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
