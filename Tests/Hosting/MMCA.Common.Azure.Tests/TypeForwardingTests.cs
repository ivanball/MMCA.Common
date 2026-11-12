using AwesomeAssertions;

namespace MMCA.Common.Azure.Tests;

/// <summary>
/// Pins the binary-compatibility half of the start-up package move: Key Vault configuration, the
/// Azure Monitor exporter and the DataProtection key ring now live in
/// <c>MMCA.Common.Azure.Startup</c>, and <c>MMCA.Common.Azure</c> forwards all three, so a host
/// compiled against the old Azure assembly still resolves them at run time without a rebuild.
/// </summary>
public sealed class TypeForwardingTests
{
    [Fact]
    public void AzureAssembly_ForwardsTheThreeStartupTypes()
    {
        Type[] moved =
        [
            typeof(AzureKeyVaultConfigurationExtensions),
            typeof(AzureMonitorExporterExtensions),
            typeof(AzureDataProtectionExtensions),
        ];

        var forwarded = typeof(AzureServiceCollectionExtensions).Assembly.GetForwardedTypes();

        forwarded.Should().Contain(moved);
    }

    [Fact]
    public void ForwardedTypes_NoLongerLiveInTheAzureAssembly()
    {
        var azureAssembly = typeof(AzureServiceCollectionExtensions).Assembly;

        typeof(AzureKeyVaultConfigurationExtensions).Assembly.Should().NotBeSameAs(azureAssembly);
        typeof(AzureMonitorExporterExtensions).Assembly.Should().NotBeSameAs(azureAssembly);
        typeof(AzureDataProtectionExtensions).Assembly.Should().NotBeSameAs(azureAssembly);
    }
}
