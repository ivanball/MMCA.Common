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

    /// <summary>
    /// The implementation behind a <c>[ServiceContract]</c> interface stays internal to the service
    /// that owns it. The contract interface is the published surface (ADR-007); the class that serves
    /// it is an implementation detail, and a public one invites a consumer to reference the concrete
    /// type, new it up, or subclass it. Every such reference is a line the extraction has to sever
    /// later, because a class cannot cross a process boundary and an interface can.
    /// </summary>
    /// <remarks>
    /// Visibility is judged with <see cref="Type.IsVisible"/>, so a public class nested inside an
    /// internal one is correctly treated as unreachable and passes. Abstract classes are exempt: an
    /// abstract base carrying shared behavior for several implementations is not itself a resolvable
    /// implementation, and a repo whose consumers subclass it is making that extension point
    /// deliberate.
    /// <para>
    /// Like the purity rule above, this is attribute-driven and vacuous until the first interface is
    /// marked, and the marker is matched by name so the package keeps its zero-reference stance
    /// toward the framework assemblies.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedPublicImplementations">
    /// Type full names or namespace prefixes where a public implementation is the deliberate, reviewed
    /// choice (a shipped default a consumer is meant to construct, a test double in a testing package).
    /// An empty list requires every implementation to be non-public.
    /// </param>
    public static void ServiceContractImplementationsAreNotPublic(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedPublicImplementations)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedPublicImplementations);

        var violations = new List<string>();

        foreach (var assembly in map.Layers.Select(l => l.Assembly).Distinct())
        {
            foreach (var type in assembly.ConcreteClasses.Where(t => t.IsVisible))
            {
                if (IsAllowed(type.FullName ?? type.Name, allowedPublicImplementations))
                {
                    continue;
                }

                var contract = ServiceContractInterfaceOf(type);
                if (contract is not null)
                {
                    violations.Add(
                        $"  - {type.FullName}: implements the [ServiceContract] interface {contract.Name} but is public");
                }
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "a [ServiceContract] interface is the published surface of an extracted service (ADR-007) "
                + "and the class serving it is an implementation detail: a public implementation lets a "
                + "consumer bind to the concrete type, which is exactly the coupling the contract exists "
                + "to prevent");
    }

    /// <summary>The first <c>[ServiceContract]</c>-marked interface a type implements, or null.</summary>
    private static Type? ServiceContractInterfaceOf(Type type) =>
        Array.Find(type.GetInterfaces(), CarriesServiceContractAttribute);

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

    /// <summary>
    /// The reflection twin of the overload above, for the rules that walk loaded types rather than
    /// NetArchTest's Cecil model.
    /// </summary>
    private static bool CarriesServiceContractAttribute(Type contract) =>
        contract.GetCustomAttributesData().Any(attribute => string.Equals(
            attribute.AttributeType.FullName,
            ServiceContractAttributeFullName,
            StringComparison.Ordinal));
}
