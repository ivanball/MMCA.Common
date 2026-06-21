using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// PII right-to-erasure rule ([Pii] ⇒ IAnonymizable, ADR-005), driven by the shared
/// <see cref="PiiConventionTestsBase"/>. Vacuous in the framework today and fails the build the moment a
/// PII-bearing entity is added without an erasure path.
/// </summary>
public sealed class PiiConventionTests : PiiConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
