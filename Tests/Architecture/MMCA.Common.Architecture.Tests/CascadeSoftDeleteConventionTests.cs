using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Cascade-soft-delete convention (an aggregate root that owns children deletes them in its own
/// <c>Delete()</c> override), driven by the shared
/// <see cref="CascadeSoftDeleteConventionTestsBase"/>. MMCA.Common's <c>Source/</c> declares no
/// child-bearing aggregate today, so the framework run is a ratchet rather than an assertion: it
/// fails the moment the framework itself grows an aggregate whose children would be left active by
/// a delete, and it is the same rule Store and ADC subclass over their own modules. The rule's own
/// behaviour is proven against compiled fixtures in <c>CascadeSoftDeleteFitnessTests</c>.
/// </summary>
public sealed class CascadeSoftDeleteConventionTests : CascadeSoftDeleteConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
