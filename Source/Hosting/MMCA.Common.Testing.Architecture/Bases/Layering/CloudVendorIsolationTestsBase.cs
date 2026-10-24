namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// The cloud-vendor fitness function: the assemblies a repo declares cloud-neutral name no Azure or
/// AWS SDK type, reference no vendor SDK or broker-transport assembly, and never reference a vendor
/// adapter package (<c>MMCA.Common.Azure</c>, <c>MMCA.Common.Aws</c>, <c>MMCA.Common.Testing.Azure</c>).
/// <para>
/// The runtime half of <c>EnforceCloudVendorBoundary</c> in
/// <c>Source/Build/MMCA.Common.LayerEnforcement.targets</c>: the build guard judges the declared
/// package references, this judges the compiled assemblies, so a vendor type that arrives through a
/// transitive path is caught too. Subclass it with the assemblies to hold and any engine allowance.
/// </para>
/// </summary>
public abstract class CloudVendorIsolationTestsBase
{
    /// <summary>Gets the assemblies that must stay cloud-neutral.</summary>
    protected abstract IEnumerable<Assembly> CloudNeutralAssemblies { get; }

    /// <summary>
    /// Gets, per assembly name, the vendor prefixes it may still name (a database engine such as
    /// Cosmos DB, never a cloud service). Empty by default.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, IReadOnlyCollection<string>> AllowedVendorPrefixes { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    [Fact]
    public void CloudNeutralAssemblies_ShouldNotName_ACloudVendorType() =>
        ArchitectureRules.CloudNeutralAssembliesNameNoVendorType(CloudNeutralAssemblies, AllowedVendorPrefixes);

    [Fact]
    public void CloudNeutralAssemblies_ShouldNotReference_ACloudVendorAssembly() =>
        ArchitectureRules.CloudNeutralAssembliesReferenceNoVendorAssembly(CloudNeutralAssemblies, AllowedVendorPrefixes);
}
