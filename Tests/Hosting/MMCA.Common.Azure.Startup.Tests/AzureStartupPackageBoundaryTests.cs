using AwesomeAssertions;

namespace MMCA.Common.Azure.Startup.Tests;

/// <summary>
/// The runtime half of the start-up package boundary (the compile-time half is
/// <c>EnforceAzureStartupBoundary</c>). A gateway or any other host that only needs Key Vault
/// configuration, the Azure Monitor exporter and the Blob + Key Vault DataProtection key ring
/// references this package instead of the full Azure adapter, so it must carry no persistence or
/// messaging dependency: that unused weight is the whole reason the package exists.
/// <para>
/// The three types keep the <c>MMCA.Common.Azure</c> namespace on purpose, so
/// <c>using MMCA.Common.Azure;</c> compiles unchanged in every consumer after the move.
/// </para>
/// </summary>
public sealed class AzureStartupPackageBoundaryTests
{
    private const string StartupAssemblyName = "MMCA.Common.Azure.Startup";

    private const string AzureNamespace = "MMCA.Common.Azure";

    // Rule 21.3: the only MMCA.Common assemblies the start-up package may reference.
    private static readonly string[] AllowedCommonAssemblies =
    [
        "MMCA.Common.Aspire",
        "MMCA.Common.Shared",
    ];

    // Rule 21.3 and the 21(c) list: no persistence, messaging, Service Bus, Notification Hubs or
    // Blob SDK assembly may appear in the start-up package's reference list.
    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "MMCA.Common.Infrastructure",
        "MMCA.Common.Application",
        "MMCA.Common.Domain",
        "MMCA.Common.API",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "Azure.Messaging",
        "Azure.Storage",
        "Microsoft.Azure.NotificationHubs",
    ];

    [Fact]
    public void ThreeExtensionTypes_LiveInTheStartupAssembly_UnderTheAzureNamespace()
    {
        Type[] types =
        [
            typeof(AzureKeyVaultConfigurationExtensions),
            typeof(AzureMonitorExporterExtensions),
            typeof(AzureDataProtectionExtensions),
        ];

        foreach (var type in types)
        {
            type.Assembly.GetName().Name.Should().Be(
                StartupAssemblyName,
                because: $"{type.Name} moved to the start-up package");
            type.Namespace.Should().Be(
                AzureNamespace,
                because: $"{type.Name} keeps its namespace so consumer using directives compile unchanged");
        }
    }

    [Fact]
    public void StartupAssembly_ReferencesNoPersistenceOrMessagingAssembly()
    {
        var referenced = ReferencedAssemblyNames();

        foreach (var prefix in ForbiddenReferencePrefixes)
        {
            referenced.Should().NotContain(
                name => name.StartsWith(prefix, StringComparison.Ordinal),
                because: $"the start-up package must not drag {prefix} into a host that only needs start-up configuration");
        }
    }

    [Fact]
    public void StartupAssembly_ReferencesNoCommonAssemblyButAspireAndShared()
    {
        var commonReferences = ReferencedAssemblyNames()
            .Where(name => name.StartsWith("MMCA.Common.", StringComparison.Ordinal));

        commonReferences.Should().OnlyContain(
            name => AllowedCommonAssemblies.Contains(name, StringComparer.Ordinal),
            because: "the start-up package sits on the service defaults and the shared kernel only");
    }

    private static List<string> ReferencedAssemblyNames() =>
    [
        .. typeof(AzureKeyVaultConfigurationExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty),
    ];
}
