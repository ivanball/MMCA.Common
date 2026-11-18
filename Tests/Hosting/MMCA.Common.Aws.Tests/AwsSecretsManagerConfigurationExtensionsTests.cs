using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.Secrets;
using MMCA.Common.Tests.Shared.Hosting;

namespace MMCA.Common.Aws.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddAwsSecretsManagerConfiguration</c>: no
/// <c>SecretsManager:NamePrefix</c> means the host gains no configuration source, no AWS dependency
/// and no tripwire receipt, while a configured prefix appends the Secrets Manager source. The
/// complete-gate and reload-interval cases shared with the Secret Manager extension run from
/// <see cref="SecretStoreConfigurationExtensionsTestsBase"/>; the cases below are Secrets Manager-only.
/// <para>
/// No test here touches the network, for the same reason as the Key Vault sibling's tests: the cases
/// that must NOT add a source run against a real <see cref="HostApplicationBuilder"/>, and the case
/// that DOES add one runs against a builder double whose configuration collects sources without
/// loading them, because a real <c>ConfigurationManager</c> loads each source the instant it is added.
/// </para>
/// </summary>
public sealed class AwsSecretsManagerConfigurationExtensionsTests : SecretStoreConfigurationExtensionsTestsBase
{
    protected override string Section => "SecretsManager";

    protected override Type SourceType => typeof(AwsSecretsManagerConfigurationSource);

    protected override string MarkerName => HostIntegrationNames.AwsSecretsManagerConfiguration;

    protected override Dictionary<string, string?> CompleteGate() => new(StringComparer.Ordinal)
    {
        ["SecretsManager:NamePrefix"] = "adc/prod/",
        ["SecretsManager:Region"] = "us-east-1",
    };

    protected override void AddConfiguration(IHostApplicationBuilder builder) =>
        builder.AddAwsSecretsManagerConfiguration();

    protected override TimeSpan? ReloadIntervalOf(IConfigurationSource source) =>
        ((AwsSecretsManagerConfigurationSource)source).ReloadInterval;

    protected override void AssertGateCarried(IConfigurationSource source) =>
        ((AwsSecretsManagerConfigurationSource)source).NamePrefix.Should().Be("adc/prod/");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAwsSecretsManagerConfiguration_WithoutANamePrefix_IsANoOp(string? prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = prefix,
            ["SecretsManager:Region"] = "us-east-1",
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        builder.AddAwsSecretsManagerConfiguration();

        builder.Configuration.Sources.Count.Should().Be(
            sourceCountBefore,
            because: "an unconfigured host must gain no configuration source, so local development and tests never reach for AWS at startup");
        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that added nothing leaves no receipt, so a prefix that appears later still trips the startup check");
    }

    [Theory]
    [InlineData("AKIDEXAMPLE", null)]
    [InlineData(null, "secret")]
    public void AddAwsSecretsManagerConfiguration_WithHalfAKeyPair_FailsNamingTheSection(string? accessKeyId, string? secret)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
            ["SecretsManager:Region"] = "us-east-1",
            ["SecretsManager:AccessKeyId"] = accessKeyId,
            ["SecretsManager:SecretAccessKey"] = secret,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => builder.AddAwsSecretsManagerConfiguration();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SecretsManager:AccessKeyId and SecretsManager:SecretAccessKey must be set together*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
    }
}
