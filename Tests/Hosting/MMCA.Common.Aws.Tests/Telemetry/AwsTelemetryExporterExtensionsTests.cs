using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace MMCA.Common.Aws.Tests.Telemetry;

/// <summary>
/// Item 32, <c>AddAwsTelemetryExporter()</c> (the AWS twin of
/// <c>MMCA.Common.Gcp.Tests/Telemetry/GoogleCloudTelemetryExporterExtensionsTests.cs</c>): refuses to run
/// ahead of the service defaults, does nothing without <c>AwsTelemetry:Region</c>, and once configured
/// leaves the receipt the startup tripwire checks, stamps <c>cloud.provider</c> and <c>cloud.region</c>
/// on the resource, and validates the key pair, the log group and stream pair and the endpoint overrides.
/// The keys are the documentation example key id with a placeholder secret.
/// </summary>
public sealed class AwsTelemetryExporterExtensionsTests
{
    private const string Region = "eu-west-1";

    [Fact]
    public void BeforeServiceDefaults_Throws_NamingTheOrder()
    {
        var builder = BuilderWith(new() { ["AwsTelemetry:Region"] = Region });

        var act = () => builder.AddAwsTelemetryExporter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddServiceDefaults()*");
    }

    [Fact]
    public void BeforeServiceDefaults_Throws_EvenWithoutARegion()
    {
        // Enforced whether or not the gate is set, so a misordered host fails on a developer machine
        // rather than only in the deployment that sets the region.
        var builder = BuilderWith([]);

        var act = () => builder.AddAwsTelemetryExporter();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddServiceDefaults()*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutARegion_RegistersNothing(string? region)
    {
        // The other keys are set, half-set or invalid on purpose: without the gate none of them is read.
        var builder = BuilderWith(new()
        {
            ["AwsTelemetry:Region"] = region,
            ["AwsTelemetry:AccessKeyId"] = "AKIDEXAMPLE",
            ["AwsTelemetry:LogGroup"] = "mmca-group",
            ["AwsTelemetry:TracesEndpoint"] = "not a url",
        });
        builder.AddServiceDefaults();
        var before = builder.Services.Count;

        builder.AddAwsTelemetryExporter();

        builder.Services.Should().HaveCount(before, "an unconfigured host takes no AWS dependency and no exporter");
        builder.Services.Should().NotContain(
            descriptor => descriptor.ImplementationInstance is HostIntegrationMarker
                && ((HostIntegrationMarker)descriptor.ImplementationInstance).Name == HostIntegrationNames.AwsTelemetryExporter,
            "a call that registered nothing leaves no receipt");
    }

    [Fact]
    public void WithARegion_RegistersTheReceipt_AndSatisfiesTheTripwire()
    {
        var builder = ConfiguredBuilder();

        builder.Services.Should().Contain(
            descriptor => descriptor.ImplementationInstance is HostIntegrationMarker
                && ((HostIntegrationMarker)descriptor.ImplementationInstance).Name == HostIntegrationNames.AwsTelemetryExporter);
        HostIntegrationNames.AwsTelemetryExporter.Should().Be("AwsTelemetryExporter");

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().NotThrow();
    }

    [Fact]
    public void RegionWithoutTheExporter_FailsTheHost_NamingTheCall()
    {
        var builder = BuilderWith(new() { ["AwsTelemetry:Region"] = Region });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage("*AwsTelemetry:Region*MMCA.Common.Aws*AddAwsTelemetryExporter()*");
    }

    [Theory]
    [InlineData("AwsTelemetry:AccessKeyId")]
    [InlineData("AwsTelemetry:SecretAccessKey")]
    public void HalfAKeyPair_Throws(string onlyKey)
    {
        var builder = BuilderWith(new()
        {
            ["AwsTelemetry:Region"] = Region,
            [onlyKey] = onlyKey.EndsWith("AccessKeyId", StringComparison.Ordinal) ? "AKIDEXAMPLE" : "secret",
        });
        builder.AddServiceDefaults();

        var act = () => builder.AddAwsTelemetryExporter();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AwsTelemetry:AccessKeyId*AwsTelemetry:SecretAccessKey*");
    }

    [Theory]
    [InlineData("AwsTelemetry:LogGroup", "mmca-group")]
    [InlineData("AwsTelemetry:LogStream", "mmca-stream")]
    public void LogGroupWithoutLogStream_Throws_NamingBothKeys(string onlyKey, string value)
    {
        var builder = BuilderWith(new()
        {
            ["AwsTelemetry:Region"] = Region,
            ["AwsTelemetry:AccessKeyId"] = "AKIDEXAMPLE",
            ["AwsTelemetry:SecretAccessKey"] = "secret",
            [onlyKey] = value,
        });
        builder.AddServiceDefaults();

        var act = () => builder.AddAwsTelemetryExporter();

        act.Should().Throw<InvalidOperationException>().Which.Message
            .Should().Contain("AwsTelemetry:LogGroup").And.Contain("AwsTelemetry:LogStream");
    }

    [Theory]
    [InlineData("AwsTelemetry:TracesEndpoint", "xray.eu-west-1.amazonaws.com/v1/traces")]
    [InlineData("AwsTelemetry:TracesEndpoint", "ftp://xray.eu-west-1.amazonaws.com/v1/traces")]
    [InlineData("AwsTelemetry:LogsEndpoint", "logs.eu-west-1.amazonaws.com/v1/logs")]
    [InlineData("AwsTelemetry:LogsEndpoint", "ftp://logs.eu-west-1.amazonaws.com/v1/logs")]
    public void AnInvalidEndpoint_Throws_NamingTheKey(string key, string configured)
    {
        // The log pair is set so the logs endpoint is in use; the rule does not say whether an unused
        // LogsEndpoint is validated, so no test depends on that.
        var builder = BuilderWith(new()
        {
            ["AwsTelemetry:Region"] = Region,
            ["AwsTelemetry:AccessKeyId"] = "AKIDEXAMPLE",
            ["AwsTelemetry:SecretAccessKey"] = "secret",
            ["AwsTelemetry:LogGroup"] = "mmca-group",
            ["AwsTelemetry:LogStream"] = "mmca-stream",
            [key] = configured,
        });
        builder.AddServiceDefaults();

        var act = () => builder.AddAwsTelemetryExporter();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    // Ruling M1: with no static key pair the default chain is warmed while the host starts. Only the
    // registration is inspected; the host is not built, so nothing reaches the real chain.
    [Fact]
    public void WithoutStaticKeys_RegistersTheCredentialWarmup()
    {
        var builder = BuilderWith(new() { ["AwsTelemetry:Region"] = Region });
        builder.AddServiceDefaults();

        builder.AddAwsTelemetryExporter();

        builder.Services.Should().ContainSingle(descriptor => descriptor.ServiceType == typeof(AwsTelemetryCredentialWarmup));
        builder.Services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory != null
            && descriptor.ImplementationFactory.Method.ReturnType == typeof(AwsTelemetryCredentialWarmup));
    }

    [Fact]
    public void WithStaticKeys_RegistersNoWarmup()
    {
        var builder = ConfiguredBuilder();

        builder.Services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(AwsTelemetryCredentialWarmup));
    }

    [Fact]
    public void WithoutARegion_RegistersNoWarmup()
    {
        var builder = BuilderWith([]);
        builder.AddServiceDefaults();

        builder.AddAwsTelemetryExporter();

        builder.Services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(AwsTelemetryCredentialWarmup));
    }

    [Fact]
    public void TheResource_CarriesCloudProviderAndRegion()
    {
        using var host = ConfiguredBuilder().Build();

        var resource = host.Services.GetRequiredService<TracerProvider>().GetResource();

        resource.Attributes.Should().Contain(new KeyValuePair<string, object>("cloud.provider", "aws"));
        resource.Attributes.Should().Contain(new KeyValuePair<string, object>("cloud.region", Region));
    }

    [Fact]
    public void TheRegion_IsTrimmed()
    {
        // Rule 3: the value is trimmed before it reaches RegionEndpoint.GetBySystemName and the resource.
        var builder = BuilderWith(new()
        {
            ["AwsTelemetry:Region"] = "  " + Region + "  ",
            ["AwsTelemetry:AccessKeyId"] = "AKIDEXAMPLE",
            ["AwsTelemetry:SecretAccessKey"] = "secret",
        });
        builder.AddServiceDefaults();
        builder.AddAwsTelemetryExporter();
        using var host = builder.Build();

        host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .Should().Contain(new KeyValuePair<string, object>("cloud.region", Region));
    }

    [Fact]
    public void TheDefaultEndpoints_AreRegional()
    {
        // DESIGN-QUESTION: (c) names an "internal resolver" without naming it. These two names are the
        // test writer's choice, shaped like the Google twin's ResolveEndpoint(IConfiguration).
        var configuration = ConfigurationWith([]);

        AwsTelemetryExporterExtensions.ResolveTracesEndpoint(configuration, Region)
            .Should().Be(new Uri("https://xray.eu-west-1.amazonaws.com/v1/traces"));
        AwsTelemetryExporterExtensions.ResolveLogsEndpoint(configuration, Region)
            .Should().Be(new Uri("https://logs.eu-west-1.amazonaws.com/v1/logs"));
    }

    [Fact]
    public void TheEndpointOverrides_AreUsedAsTheFullUrl()
    {
        // Rule 6: an override is the FULL signal URL, not a root the path is appended to.
        var configuration = ConfigurationWith(new()
        {
            ["AwsTelemetry:TracesEndpoint"] = "http://127.0.0.1:4318/custom/traces",
            ["AwsTelemetry:LogsEndpoint"] = "http://127.0.0.1:4318/custom/logs",
        });

        AwsTelemetryExporterExtensions.ResolveTracesEndpoint(configuration, Region)
            .Should().Be(new Uri("http://127.0.0.1:4318/custom/traces"));
        AwsTelemetryExporterExtensions.ResolveLogsEndpoint(configuration, Region)
            .Should().Be(new Uri("http://127.0.0.1:4318/custom/logs"));
    }

    private static HostApplicationBuilder ConfiguredBuilder()
    {
        // Static placeholder keys, so no test here reaches for the default credential chain.
        var builder = BuilderWith(new()
        {
            ["AwsTelemetry:Region"] = Region,
            ["AwsTelemetry:AccessKeyId"] = "AKIDEXAMPLE",
            ["AwsTelemetry:SecretAccessKey"] = "secret",
        });
        builder.AddServiceDefaults();
        builder.AddAwsTelemetryExporter();
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
