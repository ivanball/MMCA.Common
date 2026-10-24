using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire;

namespace MMCA.Common.Azure.Tests.Telemetry;

/// <summary>
/// <c>AddAzureMonitorExporter()</c>: refuses to run ahead of the service defaults whose filter
/// processors must precede it, and satisfies the core startup tripwire that fails a host carrying an
/// Application Insights connection string without it. The KeyVault and DataProtection receipts are
/// asserted the same way.
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
    public void KeyVaultAndDataProtectionCalls_SatisfyTheTripwire()
    {
        // Both are gated on configuration, so blank values keep the test Azure-free while the calls
        // still leave their receipts; the keys the tripwire reads are set through a later source.
        var builder = BuilderWith([]);
        builder.AddServiceDefaults();
        builder.AddAzureKeyVaultConfiguration();
        builder.AddAzureDataProtection();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:Uri"] = "https://vault.example.invalid/",
            ["DataProtection:BlobStorageUri"] = "https://blob.example.invalid/keys/keys.xml",
        });

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().NotThrow();
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
