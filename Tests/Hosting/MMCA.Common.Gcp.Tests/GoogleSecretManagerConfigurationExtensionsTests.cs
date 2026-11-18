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
/// gate appends the Secret Manager source. The complete-gate and reload-interval cases shared with the
/// Secrets Manager extension run from <see cref="SecretStoreConfigurationExtensionsTestsBase"/>; the
/// incomplete-gate case below is Secret Manager-only.
/// <para>
/// No test here touches the network, for the same reason as the Key Vault and Secrets Manager
/// siblings' tests: the cases that must NOT add a source run against a real
/// <see cref="HostApplicationBuilder"/>, and the case that DOES add one runs against a builder double
/// whose configuration collects sources without loading them, because a real
/// <c>ConfigurationManager</c> loads each source the instant it is added.
/// </para>
/// </summary>
public sealed class GoogleSecretManagerConfigurationExtensionsTests : SecretStoreConfigurationExtensionsTestsBase
{
    protected override string Section => "GoogleSecretManager";

    protected override Type SourceType => typeof(GoogleSecretManagerConfigurationSource);

    protected override string MarkerName => HostIntegrationNames.GoogleSecretManagerConfiguration;

    protected override Dictionary<string, string?> CompleteGate() => new(StringComparer.Ordinal)
    {
        ["GoogleSecretManager:ProjectId"] = "mmca-prod",
        ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
    };

    protected override void AddConfiguration(IHostApplicationBuilder builder) =>
        builder.AddGoogleSecretManagerConfiguration();

    protected override TimeSpan? ReloadIntervalOf(IConfigurationSource source) =>
        ((GoogleSecretManagerConfigurationSource)source).ReloadInterval;

    protected override void AssertGateCarried(IConfigurationSource source)
    {
        var secretManager = (GoogleSecretManagerConfigurationSource)source;
        secretManager.ProjectId.Should().Be("mmca-prod");
        secretManager.NamePrefix.Should().Be("adc-prod-");
    }

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
}
