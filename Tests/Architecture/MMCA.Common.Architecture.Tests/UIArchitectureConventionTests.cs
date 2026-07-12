using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// UI-architecture convention rules (§18), driven by the shared
/// <see cref="UIArchitectureConventionTestsBase"/>: the framework's shared pages and primitives keep
/// code-behind files within the convention cap and keep inline <c>@code</c> blocks small, so the
/// container/presentational split is CI-enforced rather than review-enforced.
/// </summary>
public sealed class UIArchitectureConventionTests : UIArchitectureConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
