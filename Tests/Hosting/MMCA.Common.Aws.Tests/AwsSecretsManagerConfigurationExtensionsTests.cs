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
/// and no tripwire receipt, while a configured prefix appends the Secrets Manager source.
/// <para>
/// No test here touches the network, for the same reason as the Key Vault sibling's tests: the cases
/// that must NOT add a source run against a real <see cref="HostApplicationBuilder"/>, and the case
/// that DOES add one runs against a builder double whose configuration collects sources without
/// loading them, because a real <c>ConfigurationManager</c> loads each source the instant it is added.
/// </para>
/// </summary>
public sealed class AwsSecretsManagerConfigurationExtensionsTests
{
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

    [Fact]
    public void AddAwsSecretsManagerConfiguration_WithANamePrefix_AppendsTheSourceAndTheReceipt()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
            ["SecretsManager:Region"] = "eu-west-1",
            ["SecretsManager:ReloadIntervalMinutes"] = "15",
        });

        builder.AddAwsSecretsManagerConfiguration();

        var source = builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<AwsSecretsManagerConfigurationSource>().Subject;
        source.NamePrefix.Should().Be("adc/prod/");
        source.ReloadInterval.Should().Be(TimeSpan.FromMinutes(15));
        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.AwsSecretsManagerConfiguration);
    }

    [Fact]
    public void AddAwsSecretsManagerConfiguration_WithoutAReloadInterval_ReadsOnce()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
        });

        builder.AddAwsSecretsManagerConfiguration();

        builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<AwsSecretsManagerConfigurationSource>()
            .Which.ReloadInterval.Should().BeNull();
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

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("fifteen")]
    [InlineData("1.5")]
    public void AddAwsSecretsManagerConfiguration_WithAnInvalidReloadInterval_Throws(string reloadInterval)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
            ["SecretsManager:Region"] = "us-east-1",
            ["SecretsManager:ReloadIntervalMinutes"] = reloadInterval,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => builder.AddAwsSecretsManagerConfiguration();

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly rather than quietly fall back to never reloading")
            .WithMessage("*SecretsManager:ReloadIntervalMinutes*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
    }
}
