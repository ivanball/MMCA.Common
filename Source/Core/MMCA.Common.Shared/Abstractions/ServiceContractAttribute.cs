namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Marks a type as part of the published wire surface of a module that can run as its own service
/// (see ADR-007). The marked type is typically the cross-module service interface a module declares in
/// its <c>Shared</c> project, satisfied in-process by the owning module and, across the network, by a
/// gRPC adapter in the service's <c>*.Contracts</c> project.
/// The invariant (contract types must not depend on the producing service's <c>Domain</c>,
/// <c>Application</c>, or <c>Infrastructure</c>) is enforced directly by the dedicated
/// <c>ServiceContractPurityTestsBase</c> fitness rule, which scans every mapped assembly for types
/// carrying this attribute, alongside the transport- and layer-purity rules that guard the same
/// boundary from the layer side (ADR-015). The attribute is an adoptable marker that MMCA.Common
/// itself applies to no type (the framework ships no <c>[ServiceContract]</c> types), so the rule is
/// a ratchet here and bites in a repo the moment its first contract type is marked.
/// <para>
/// Apply it to the C# interface that consumers depend on (e.g. <c>IProductVariantService</c>); the
/// attribute also accepts classes and structs, but the reference consumers mark interfaces only (no
/// integration event record or DTO carries it). Generated gRPC client classes do not need this attribute: they are part of the contract surface by
/// virtue of being declared in a <c>.proto</c> file.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ServiceContractAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceContractAttribute"/> class with no version.
    /// </summary>
    public ServiceContractAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceContractAttribute"/> class.
    /// </summary>
    /// <param name="version">Optional contract version (e.g. <c>"v1"</c>). Defaults to <c>v1</c> when omitted.</param>
    public ServiceContractAttribute(string version) => Version = version;

    /// <summary>Gets the contract version. Defaults to <c>v1</c>.</summary>
    public string Version { get; } = "v1";
}
