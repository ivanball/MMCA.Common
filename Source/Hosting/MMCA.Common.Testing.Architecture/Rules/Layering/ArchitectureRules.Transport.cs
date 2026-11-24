namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Transport namespaces that must never leak into Domain/Application/Shared: the broker clients the
    /// framework's transports run on (RabbitMQ, Azure Service Bus, Amazon SQS and SNS), MassTransit
    /// (banned outright, ADR-118), and the gRPC/protobuf runtime. Keeping these at the edges
    /// (Infrastructure, the vendor adapters, <c>*.Service</c>, <c>*.Contracts</c>) is what keeps a
    /// module behaving identically in-process or extracted, so service extraction stays reversible
    /// (ADR-006/007/008). "Grpc" matches Grpc.Core/Grpc.Net.*/Grpc.AspNetCore but not
    /// "MMCA.Common.Grpc".
    /// </summary>
    public static readonly IReadOnlyList<string> TransportDependencies =
    [
        "MassTransit",
        "RabbitMQ.Client",
        "Azure.Messaging.ServiceBus",
        "Amazon.SQS",
        "Amazon.SimpleNotificationService",
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
            var result = ArchitectureTypes.InAssembly(layerRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden);

            ArchitectureAssert.NoViolations(result,
                $"{layerRef.RootNamespace}: {layerRef.Layer} must stay transport-agnostic — depend on "
                    + "IEventBus and the module's own interfaces (gRPC adapters live at the edge), not a broker client, Grpc or Protobuf directly");
        }
    }
}
