using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.Secrets;
using MMCA.Common.Tests.Shared.Hosting;

namespace MMCA.Common.Gcp.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddGoogleSecretManagerConfiguration</c>: unless both
/// <c>GoogleSecretManager:ProjectId</c> and <c>GoogleSecretManager:NamePrefix</c> are set, the host
/// gains no configuration source, no Google dependency and no tripwire receipt, while a complete
/// gate appends the Secret Manager source.
/// <para>
/// No test here touches the network, for the same reason as the Key Vault and Secrets Manager
/// siblings' tests: the cases that must NOT add a source run against a real
/// <see cref="HostApplicationBuilder"/>, and the case that DOES add one runs against a builder double
/// whose configuration collects sources without loading them, because a real
/// <c>ConfigurationManager</c> loads each source the instant it is added.
/// </para>
/// </summary>
public sealed class GoogleSecretManagerConfigurationExtensionsTests
{
    [Theory]
    [InlineData(null, "adc-prod-")]
    [InlineData("", "adc-prod-")]
    [InlineData("   ", "adc-prod-")]
    [InlineData("mmca-prod", null)]
    [InlineData("mmca-prod", "")]
    [InlineData("mmca-prod", "   ")]
    [InlineData(null, null)]
    public void AddGoogleSecretManagerConfiguration_WithAnIncompleteGate_IsANoOp(string? projectId, string? prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = projectId,
            ["GoogleSecretManager:NamePrefix"] = prefix,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        builder.AddGoogleSecretManagerConfiguration();

        builder.Configuration.Sources.Count.Should().Be(
            sourceCountBefore,
            because: "an unconfigured host must gain no configuration source, so local development and tests never reach for Google Cloud at startup");
        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that added nothing leaves no receipt, so a prefix that appears later still trips the startup check");
    }

    [Fact]
    public void AddGoogleSecretManagerConfiguration_WithACompleteGate_AppendsTheSourceAndTheReceipt()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = "mmca-prod",
            ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
            ["GoogleSecretManager:ReloadIntervalMinutes"] = "15",
        });

        builder.AddGoogleSecretManagerConfiguration();

        var source = builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<GoogleSecretManagerConfigurationSource>().Subject;
        source.ProjectId.Should().Be("mmca-prod");
        source.NamePrefix.Should().Be("adc-prod-");
        source.ReloadInterval.Should().Be(TimeSpan.FromMinutes(15));
        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.GoogleSecretManagerConfiguration);
    }

    [Fact]
    public void AddGoogleSecretManagerConfiguration_WithoutAReloadInterval_ReadsOnce()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = "mmca-prod",
            ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
        });

        builder.AddGoogleSecretManagerConfiguration();

        builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<GoogleSecretManagerConfigurationSource>()
            .Which.ReloadInterval.Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("fifteen")]
    [InlineData("1.5")]
    public void AddGoogleSecretManagerConfiguration_WithAnInvalidReloadInterval_Throws(string reloadInterval)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = "mmca-prod",
            ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
            ["GoogleSecretManager:ReloadIntervalMinutes"] = reloadInterval,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => builder.AddGoogleSecretManagerConfiguration();

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly rather than quietly fall back to never reloading")
            .WithMessage("*GoogleSecretManager:ReloadIntervalMinutes*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
        builder.Services.Should().NotContain(d => d.ImplementationInstance is HostIntegrationMarker);
    }
}
