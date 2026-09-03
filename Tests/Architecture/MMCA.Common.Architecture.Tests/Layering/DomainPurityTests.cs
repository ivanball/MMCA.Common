using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// Framework-independence rules for MMCA.Common (Domain/Shared stay framework-free, Application stays
/// host-agnostic), driven by the shared <see cref="DomainPurityTestsBase"/>.
/// </summary>
public sealed class DomainPurityTests : DomainPurityTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
