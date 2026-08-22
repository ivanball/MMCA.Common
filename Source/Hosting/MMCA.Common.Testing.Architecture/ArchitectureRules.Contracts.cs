namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// The full name of the framework's <c>[ServiceContract]</c> marker (ADR-007). Matched as a string,
    /// exactly like the other framework types this package detects, so the rule library keeps its
    /// deliberate zero-reference stance toward the framework assemblies (see the csproj comment).
    /// </summary>
    public const string ServiceContractAttributeFullName =
        "MMCA.Common.Shared.Abstractions.ServiceContractAttribute";

    /// <summary>
    /// Every <c>[ServiceContract]</c> type stays pure with respect to the producing service: the wire
    /// surface of an extracted service (ADR-007) carries only Shared and contract types, never the
    /// producing service's Domain, Application or Infrastructure types. A contract that leaks a domain
    /// entity, a handler abstraction or a persistence type forces every consumer to take the producer's
    /// internals as a package dependency, which is what makes an extraction irreversible.
    /// </summary>
    /// <remarks>
    /// Attribute-driven rather than <see cref="Layer.Contracts"/>-driven: the rule scans every assembly
    /// the map registers for types carrying the marker, so it enforces the invariant wherever the
    /// contract types live and starts biting the moment the first type is marked. A repo that marks no
    /// type yet passes vacuously.
    /// <para>
    /// A marked type that lives inside a Domain, Application or Infrastructure assembly fails by
    /// construction, and that is the intent: a published contract belongs in a <c>*.Contracts</c> (or
    /// Shared) assembly, not inside the service it describes.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    public static void ServiceContractsDoNotDependOnServiceInternals(IArchitectureMap map)
    {
        var forbidden = ServiceInternalNamespaces(map);
        if (forbidden.Length == 0)
        {
            return;
        }

        foreach (var layerRef in map.Layers)
        {
            var result = Types.InAssembly(layerRef.Assembly)
                .That()
                .MeetCustomRule(CarriesServiceContractAttribute)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{layerRef.RootNamespace}: a [ServiceContract] type is the published wire surface of an "
                    + "extracted service (ADR-007), so it must expose only Shared and contract types, never "
                    + $"the producing service's internals ({string.Join(", ", forbidden)})");
        }
    }

    /// <summary>The Domain/Application/Infrastructure namespaces a contract type must never reach into.</summary>
    private static string[] ServiceInternalNamespaces(IArchitectureMap map)
    {
        Layer[] internalLayers = [Layer.Domain, Layer.Application, Layer.Infrastructure];

        return [.. map.Layers
            .Where(l => internalLayers.Contains(l.Layer))
            .Select(l => l.RootNamespace)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>True when the type carries the framework's <c>[ServiceContract]</c> marker, matched by name.</summary>
    private static bool CarriesServiceContractAttribute(Mono.Cecil.TypeDefinition type) =>
        type.HasCustomAttributes
        && type.CustomAttributes.Any(attribute => string.Equals(
            attribute.AttributeType.FullName,
            ServiceContractAttributeFullName,
            StringComparison.Ordinal));
}
