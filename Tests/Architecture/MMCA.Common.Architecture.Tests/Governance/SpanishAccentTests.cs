using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Spanish-localization rule, driven by the shared <see cref="SpanishAccentTestsBase"/>: no
/// <c>*.es.resx</c> under this repo's <c>Source/</c> tree ships a common word with its accent or
/// n-tilde missing. The framework's own UI strings are the ones every consumer inherits, so they are
/// held to the same rule the consumers' Spanish resources are.
/// </summary>
public sealed class SpanishAccentTests : SpanishAccentTestsBase
{
    protected override string ResourceRoot { get; } =
        Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"), "Source");
}
