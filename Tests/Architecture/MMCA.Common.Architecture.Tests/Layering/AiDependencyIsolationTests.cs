using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Layering;

/// <summary>
/// The model boundary, asserted against the framework's own layers: no MMCA.Common layer outside
/// Infrastructure names a language-model SDK type, and none of them references MMCA.Common.AI.
/// <para>
/// The framework holds no prompts of its own, so this run is the guard that the governed package
/// STAYS optional: the day a convenience type from MMCA.Common.AI is pulled into Application or
/// Shared, every consumer inherits a model SDK it never asked for.
/// </para>
/// </summary>
public sealed class AiDependencyIsolationTests : AiDependencyIsolationTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
