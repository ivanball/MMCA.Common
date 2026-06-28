using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// PII right-to-erasure rule ([Pii] ⇒ IAnonymizable, ADR-005), driven by the shared
/// <see cref="PiiConventionTestsBase"/>. This <em>scan</em> is structurally vacuous in the framework (no
/// data-subject type lives in MMCA.Common's Domain) and fails the build the moment a PII-bearing entity
/// is added without an erasure path. The redaction + erasure machinery it guards is proven non-vacuously
/// by <see cref="PiiErasureContractFitnessTests"/>, which forces a representative [Pii] data subject
/// through <c>PiiRedactor</c> and <c>IAnonymizable</c> end to end.
/// </summary>
public sealed class PiiConventionTests : PiiConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
