using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Transport-boundary rule (Domain/Application/Shared stay free of MassTransit/Grpc/Protobuf), driven by
/// the shared <see cref="MicroserviceExtractionTestsBase"/>. Common-framework-specific transport sanity
/// (the Grpc package's own boundaries, abstraction placement) lives in <see cref="FrameworkSanityTests"/>.
/// </summary>
public sealed class MicroserviceExtractionTests : MicroserviceExtractionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
