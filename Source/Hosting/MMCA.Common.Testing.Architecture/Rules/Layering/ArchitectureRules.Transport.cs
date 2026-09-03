namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Transport packages that must never leak into Domain/Application/Shared. Keeping these at the
    /// edges (Infrastructure, <c>*.Service</c>, <c>*.Contracts</c>) is what keeps a module behaving
    /// identically in-process or extracted, so service extraction stays reversible (ADR-006/007/008).
    /// "Grpc" matches Grpc.Core/Grpc.Net.*/Grpc.AspNetCore but not "MMCA.Common.Grpc".
    /// </summary>
    public static readonly IReadOnlyList<string> TransportDependencies =
    [
        "MassTransit",
        "Grpc",
        "Google.Protobuf",
    ];

    /// <summary>Assert Domain, Application and Shared (framework + per-module) hold no transport types.</summary>
    public static void TransportDoesNotLeakIntoCoreLayers(IArchitectureMap map)
    {
        Layer[] coreLayers = [Layer.Domain, Layer.Application, Layer.Shared];
        var forbidden = TransportDependencies.ToArray();

        foreach (var layerRef in map.Layers.Where(l => coreLayers.Contains(l.Layer)))
        {
            var result = Types.InAssembly(layerRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{layerRef.RootNamespace}: {layerRef.Layer} must stay transport-agnostic — depend on "
                    + "IMessageBus / typed gRPC client interfaces, not MassTransit/Grpc/Protobuf directly");
        }
    }
}
