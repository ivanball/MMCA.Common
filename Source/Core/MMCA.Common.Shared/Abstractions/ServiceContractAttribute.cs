namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Marks an interface or DTO as part of a service contract published in a <c>*.Contracts</c>
/// NuGet package. Used by architecture tests to enforce that contract types do not depend on
/// the producing service's <c>Domain</c>, <c>Application</c>, or <c>Infrastructure</c> projects,
/// and by tooling to identify the wire surface of an extracted microservice.
/// <para>
/// Apply to the C# interface that consumers depend on (e.g. <c>IProductVariantService</c>),
/// the integration event records that flow over the message bus (e.g. <c>ProductVariantChanged</c>),
/// and the DTOs that cross the boundary. Generated gRPC client classes do not need this attribute —
/// they are inherently part of the contract surface by virtue of being declared in a <c>.proto</c> file.
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
