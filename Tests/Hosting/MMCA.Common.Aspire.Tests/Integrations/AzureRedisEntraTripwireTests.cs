using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Aspire.Tests.Integrations;

/// <summary>
/// Security lane D1, the startup tripwire for the Redis Entra gate: a host that sets
/// <c>AzureRedis:EntraAuthentication:Enabled=true</c> without calling
/// <c>AddAzureRedisEntraAuthentication(configuration)</c> fails at startup naming that call (it would
/// otherwise keep signing in with whatever access key its connection string carries); with the
/// adapter's receipt, or with the gate off, it starts.
/// </summary>
public sealed class AzureRedisEntraTripwireTests
{
    private const string EnabledKey = "AzureRedis:EntraAuthentication:Enabled";

    private static ValidateOptionsResult Validate(Dictionary<string, string?> settings, params string[] registered)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var tripwire = new HostIntegrationTripwire(configuration, registered.Select(name => new HostIntegrationMarker(name)));
        return tripwire.Validate(Options.DefaultName, new HostIntegrationTripwireOptions());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void Enabled_WithoutTheRegistration_FailsNamingTheCall(string enabled)
    {
        var result = Validate(new() { [EnabledKey] = enabled });

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should()
            .Contain(EnabledKey)
            .And.Contain("AddAzureRedisEntraAuthentication")
            .And.Contain("MMCA.Common.Azure");
    }

    [Fact]
    public void Enabled_WithTheRegistrationReceipt_Succeeds() =>
        Validate(new() { [EnabledKey] = "true" }, HostIntegrationNames.AzureRedisEntraAuthentication)
            .Succeeded.Should().BeTrue();

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData(null)]
    public void GateOffOrAbsent_DoesNotTrip(string? enabled) =>
        Validate(new() { [EnabledKey] = enabled }).Succeeded.Should().BeTrue();

    [Fact]
    public void AddServiceDefaults_EnabledWithoutTheRegistration_FailsTheHostAtStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [EnabledKey] = "true",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = string.Empty,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = string.Empty,
        });
        builder.AddServiceDefaults();

        using var host = builder.Build();
        var validate = () => host.Services.GetRequiredService<IStartupValidator>().Validate();

        validate.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{EnabledKey}*AddAzureRedisEntraAuthentication*");
    }
}
