using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Tests.Shared.Hosting;

namespace MMCA.Common.Azure.Startup.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddAzureKeyVaultConfiguration</c>: no <c>KeyVault:Uri</c>
/// means the host gains no configuration source and takes no Azure dependency at startup, while a
/// configured URI appends the Key Vault source that later reads the vault.
/// <para>
/// No test here touches the network, and the two builder shapes are what keeps that true. The tests
/// that must NOT add a source (the gate, and the two malformed-configuration cases, which throw
/// before the source is built) run against a real <see cref="HostApplicationBuilder"/>, because that
/// is the type a host actually uses. The tests that DO add a source run against a builder double
/// whose configuration merely collects sources: a real <c>ConfigurationManager</c> builds and loads
/// every source the instant it is added, and loading the Key Vault source means a live call to the
/// vault. The double therefore proves the registration without the call, which is exactly the part
/// this extension is responsible for.
/// </para>
/// </summary>
public sealed class AzureKeyVaultConfigurationExtensionsTests
{
    private const string KeyVaultAssemblyName = "Azure.Extensions.AspNetCore.Configuration.Secrets";

    [Fact]
    public void AddAzureKeyVaultConfiguration_WithoutVaultUri_AddsNoConfigurationSource()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = builder.Configuration;
        var sourceCountBefore = configuration.Sources.Count;

        builder.AddAzureKeyVaultConfiguration();

        configuration.Sources.Count
            .Should().Be(
                sourceCountBefore,
                because: "an unconfigured host must gain no configuration source at all, so local development and tests never reach for a vault at startup");

        configuration.Sources.Any(IsKeyVaultSource)
            .Should().BeFalse(
                because: "the gate key is what decides whether the host takes an Azure dependency, and it is absent here");
    }

    [Fact]
    public void AddAzureKeyVaultConfiguration_WithVaultUri_AppendsTheKeyVaultConfigurationSource()
    {
        var builder = SourceCollectingBuilderWith(new(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "https://mmca-tests.vault.azure.net/",
        });

        builder.AddAzureKeyVaultConfiguration();

        builder.Configuration.Sources
            .Should().ContainSingle(
                because: "a configured vault URI must add exactly one configuration source");

        IsKeyVaultSource(builder.Configuration.Sources[^1])
            .Should().BeTrue(
                because: "the source has to be the Key Vault one specifically, and it has to be appended last so vault secrets override the file and environment values added before it");
    }

    [Fact]
    public void AddAzureKeyVaultConfiguration_WithReloadIntervalMinutes_StillAppendsTheKeyVaultConfigurationSource()
    {
        var builder = SourceCollectingBuilderWith(new(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "https://mmca-tests.vault.azure.net/",
            ["KeyVault:ReloadIntervalMinutes"] = "15",
        });

        builder.AddAzureKeyVaultConfiguration();

        IsKeyVaultSource(builder.Configuration.Sources[^1])
            .Should().BeTrue(
                because: "a valid reload interval must be accepted and still produce the vault source; the interval itself is carried on options held by a source type the Azure package keeps internal, so it is not observable from here without reading private state");
    }

    [Fact]
    public void AddAzureKeyVaultConfiguration_WithMalformedVaultUri_Throws()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "not-a-vault-uri",
        });

        var act = () => builder.AddAzureKeyVaultConfiguration();

        act.Should().Throw<UriFormatException>(
            because: "a typo in the vault URI must stop the host at startup rather than let it run on whatever configuration it happened to have");

        builder.Configuration.Sources.Any(IsKeyVaultSource)
            .Should().BeFalse(
                because: "the URI is validated before the source is constructed, so nothing is added and no vault call is attempted");
    }

    [Fact]
    public void AddAzureKeyVaultConfiguration_WithNonPositiveReloadIntervalMinutes_Throws()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "https://mmca-tests.vault.azure.net/",
            ["KeyVault:ReloadIntervalMinutes"] = "0",
        });

        var act = () => builder.AddAzureKeyVaultConfiguration();

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly: quietly falling back to no reload would leave a rotated secret unused until the next restart, and nobody would notice until it mattered")
            .WithMessage("*KeyVault:ReloadIntervalMinutes*");
    }

    private static bool IsKeyVaultSource(IConfigurationSource source) =>
        string.Equals(source.GetType().Assembly.GetName().Name, KeyVaultAssemblyName, StringComparison.Ordinal);

    private static SourceCollectingHostApplicationBuilder SourceCollectingBuilderWith(Dictionary<string, string?> settings) =>
        new(settings, applicationName: "MMCA.Common.Aspire.Tests");
}
