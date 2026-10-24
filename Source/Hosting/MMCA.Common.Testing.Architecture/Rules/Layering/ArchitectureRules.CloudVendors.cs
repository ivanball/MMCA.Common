namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Cloud-vendor namespaces and the framework's vendor adapter packages, none of which a
    /// cloud-neutral assembly may name: the Azure and AWS SDKs, the MassTransit transports for their
    /// brokers, and the adapters that hold them (<c>MMCA.Common.Azure</c>, <c>MMCA.Common.Aws</c>,
    /// <c>MMCA.Common.Testing.Azure</c>).
    /// <para>
    /// The adapters reference the core; the reverse edge is what turns "runs on any cloud" into "pulls
    /// one cloud's SDK, credential chain and advisory stream into every consumer".
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> CloudVendorDependencies =
    [
        "Azure.",
        "Microsoft.Azure.",
        "Amazon.",
        "MassTransit.AzureServiceBusTransport",
        "MassTransit.AmazonSqsTransport",
        "MMCA.Common.Azure",
        "MMCA.Common.Aws",
        "MMCA.Common.Testing.Azure",
    ];

    /// <summary>
    /// Assembly-name prefixes a cloud-neutral assembly may not reference: the vendor SDK assemblies,
    /// the vendor broker transports, and the adapter packages.
    /// </summary>
    public static readonly IReadOnlyList<string> CloudVendorAssemblyPrefixes =
    [
        "Azure.",
        "Microsoft.Azure.",
        "AWSSDK.",
        "MassTransit.Azure",
        "MassTransit.AmazonSqs",
        "MMCA.Common.Azure",
        "MMCA.Common.Aws",
        "MMCA.Common.Testing.Azure",
    ];

    /// <summary>
    /// Assert that no cloud-neutral assembly names a cloud-vendor or vendor-adapter type.
    /// </summary>
    /// <param name="assemblies">The assemblies that must stay cloud-neutral.</param>
    /// <param name="allowedPrefixes">
    /// Per assembly name, the vendor namespace prefixes it is allowed to name (an engine dependency
    /// such as <c>Microsoft.Azure.Cosmos</c>). Types depending on an allowed prefix are left out of the
    /// type-level check; <see cref="CloudNeutralAssembliesReferenceNoVendorAssembly"/> still holds the
    /// assembly as a whole to the same allowance.
    /// </param>
    public static void CloudNeutralAssembliesNameNoVendorType(
        IEnumerable<Assembly> assemblies,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> allowedPrefixes)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(allowedPrefixes);

        var forbidden = CloudVendorDependencies.ToArray();

        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name ?? string.Empty;
            var subjects = allowedPrefixes.TryGetValue(name, out var allowed) && allowed.Count > 0
                ? Types.InAssembly(assembly).That().DoNotHaveDependencyOnAny([.. allowed])
                : Types.InAssembly(assembly).That().ArePublic().Or().AreNotPublic();

            var result = subjects
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{name} must stay cloud-neutral: move the vendor dependency into the adapter package "
                    + "(MMCA.Common.Azure or MMCA.Common.Aws) behind a core abstraction");
        }
    }

    /// <summary>
    /// Assert that no cloud-neutral assembly references a cloud-vendor assembly or a vendor adapter
    /// assembly, apart from the allowed prefixes.
    /// </summary>
    /// <param name="assemblies">The assemblies that must stay cloud-neutral.</param>
    /// <param name="allowedPrefixes">Per assembly name, the referenced-assembly prefixes it is allowed.</param>
    public static void CloudNeutralAssembliesReferenceNoVendorAssembly(
        IEnumerable<Assembly> assemblies,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> allowedPrefixes)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(allowedPrefixes);

        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name ?? string.Empty;
            IReadOnlyCollection<string> allowed = allowedPrefixes.TryGetValue(name, out var configured) ? configured : [];

            var offending = assembly.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(reference => IsCloudVendorAssembly(reference)
                    && !allowed.Any(prefix => reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            offending.Should().BeEmpty(
                $"{name} must stay cloud-neutral, so it may not reference a cloud-vendor SDK or adapter assembly");
        }
    }

    /// <summary>Whether an assembly name is a vendor SDK, a vendor broker transport or an adapter.</summary>
    /// <param name="assemblyName">The referenced assembly's simple name.</param>
    /// <returns><see langword="true"/> for a cloud-vendor assembly.</returns>
    private static bool IsCloudVendorAssembly(string assemblyName) =>
        CloudVendorAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || assemblyName.Contains(".Azure.", StringComparison.OrdinalIgnoreCase)
        || assemblyName.EndsWith(".Azure", StringComparison.OrdinalIgnoreCase);
}
