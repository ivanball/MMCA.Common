using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace MMCA.Common.Gcp.Tests.Telemetry;

/// <summary>
/// <c>AddGoogleCloudTelemetryExporter()</c>: refuses to run ahead of the service defaults whose
/// filter processors must precede it, does nothing without <c>GoogleCloudTelemetry:ProjectId</c>,
/// and once configured leaves the receipt the core startup tripwire checks, stamps
/// <c>gcp.project_id</c> on the resource and resolves the endpoint (default or override).
/// </summary>
public sealed class GoogleCloudTelemetryExporterExtensionsTests
{
    private const string ProjectId = "mmca-prod";

    [Fact]
    public void BeforeServiceDefaults_Throws_NamingTheOrder()
    {
        var builder = BuilderWith(new() { ["GoogleCloudTelemetry:ProjectId"] = ProjectId });

        var act = () => builder.AddGoogleCloudTelemetryExporter();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddGoogleCloudTelemetryExporter() must run after AddServiceDefaults()*");
    }

    [Fact]
    public void BeforeServiceDefaults_Throws_EvenWithoutAProjectId()
    {
        // Enforced whether or not the gate is set, so a misordered host fails on a developer
        // machine rather than only in the deployment that sets the project.
        var builder = BuilderWith([]);

        var act = () => builder.AddGoogleCloudTelemetryExporter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*after AddServiceDefaults()*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAProjectId_RegistersNothing(string? projectId)
    {
        var builder = BuilderWith(new() { ["GoogleCloudTelemetry:ProjectId"] = projectId });
        builder.AddServiceDefaults();
        var before = builder.Services.Count;

        builder.AddGoogleCloudTelemetryExporter();

        builder.Services.Should().HaveCount(before, "an unconfigured host takes no Google dependency and no exporter");
        builder.Services.Should().NotContain(
            descriptor => descriptor.ImplementationInstance is HostIntegrationMarker
                && ((HostIntegrationMarker)descriptor.ImplementationInstance).Name == HostIntegrationNames.GoogleCloudTelemetryExporter,
            "a call that registered nothing leaves no receipt");
    }

    [Fact]
    public void WithAProjectId_RegistersTheReceipt_AndSatisfiesTheTripwire()
    {
        var builder = ConfiguredBuilder();

        builder.Services.Should().Contain(
            descriptor => descriptor.ImplementationInstance is HostIntegrationMarker
                && ((HostIntegrationMarker)descriptor.ImplementationInstance).Name == HostIntegrationNames.GoogleCloudTelemetryExporter);

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().NotThrow();
    }

    [Fact]
    public void ProjectIdWithoutTheExporter_FailsTheHost_NamingTheCall()
    {
        var builder = BuilderWith(new() { ["GoogleCloudTelemetry:ProjectId"] = ProjectId });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage("*GoogleCloudTelemetry:ProjectId*MMCA.Common.Gcp*AddGoogleCloudTelemetryExporter()*");
    }

    [Fact]
    public void TheResource_CarriesTheProjectId()
    {
        using var host = ConfiguredBuilder().Build();

        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource();

        resource.Attributes.Should().Contain(new KeyValuePair<string, object>("gcp.project_id", ProjectId));
    }

    [Fact]
    public void TheEndpoint_DefaultsToTheGlobalTelemetryApi()
    {
        var configuration = ConfigurationWith([]);

        GoogleCloudTelemetryExporterExtensions.ResolveEndpoint(configuration)
            .Should().Be(new Uri("https://telemetry.googleapis.com/"));
    }

    [Theory]
    [InlineData("https://telemetry.us-central1.rep.googleapis.com", "https://telemetry.us-central1.rep.googleapis.com/")]
    [InlineData("https://telemetry.us-central1.rep.googleapis.com/", "https://telemetry.us-central1.rep.googleapis.com/")]
    [InlineData("http://127.0.0.1:4318", "http://127.0.0.1:4318/")]
    public void TheEndpointOverride_IsHonoured(string configured, string expected)
    {
        var configuration = ConfigurationWith(new() { ["GoogleCloudTelemetry:Endpoint"] = configured });

        GoogleCloudTelemetryExporterExtensions.ResolveEndpoint(configuration).Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("telemetry.googleapis.com")]
    [InlineData("ftp://telemetry.googleapis.com")]
    public void AnInvalidEndpoint_Throws_NamingTheKey(string configured)
    {
        var builder = BuilderWith(new()
        {
            ["GoogleCloudTelemetry:ProjectId"] = ProjectId,
            ["GoogleCloudTelemetry:Endpoint"] = configured,
        });
        builder.AddServiceDefaults();

        var act = () => builder.AddGoogleCloudTelemetryExporter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*GoogleCloudTelemetry:Endpoint*");
    }

    [Theory]
    [InlineData("v1/traces", "https://telemetry.googleapis.com/v1/traces")]
    [InlineData("v1/logs", "https://telemetry.googleapis.com/v1/logs")]
    public void TheSignalPath_IsAppendedToTheRoot(string signalPath, string expected) =>
        GoogleCloudTelemetryExporterExtensions.SignalEndpoint(new Uri("https://telemetry.googleapis.com/"), signalPath)
            .Should().Be(new Uri(expected));

    private static HostApplicationBuilder ConfiguredBuilder()
    {
        var builder = BuilderWith(new() { ["GoogleCloudTelemetry:ProjectId"] = ProjectId });
        builder.Services.AddSingleton<IGoogleCloudTelemetryTokenSource>(new FakeTelemetryTokenSource("test-token"));
        builder.AddServiceDefaults();
        builder.AddGoogleCloudTelemetryExporter();
        return builder;
    }

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty;
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
