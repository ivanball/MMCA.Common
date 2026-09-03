namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Result-pattern purity fitness function (ADR-013): the domain returns <c>Result</c>, it does not
/// throw. A thrown business failure skips <c>Result.Combine</c> invariant composition, arrives at the
/// API as a 500 instead of the outcome the caller can act on, and pays an exception unwind on a path
/// that is not exceptional. The rule fails on any <see langword="throw"/> in the map's Domain assemblies whose
/// exception is not an argument guard (<c>ArgumentException</c>, <c>ArgumentNullException</c>,
/// <c>ArgumentOutOfRangeException</c>), which report a caller bug rather than a business outcome.
/// <para>
/// A bare <c>throw;</c> rethrow is ignored, and the <c>ArgumentNullException.ThrowIfNull</c> family
/// emits no <see langword="throw"/> in the caller at all, so the modern guard style always passes.
/// </para>
/// <para>
/// Adoption in a repo with existing domain throws: subclass, run once, and either convert each
/// reported site to a <c>Result.Failure</c> or move it into <see cref="AllowedThrowingTypes"/> with a
/// comment saying why throwing is right there. The list is the point of the rule: it turns "the
/// domain returns Result, mostly" into a reviewed inventory of every place that does not.
/// </para>
/// </summary>
public abstract class DomainThrowTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Type full names (<c>MMCA.X.Sales.Domain.Interop.LegacyBridge</c>) or namespace prefixes
    /// (<c>MMCA.X.Sales.Domain.Interop</c>) where throwing is the deliberate, reviewed choice: an
    /// ORM-facing shim, a generated partial, a guard that genuinely signals a programming error.
    /// Empty by default, which allows nothing beyond the argument guards.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedThrowingTypes => [];

    [Fact]
    public void Domain_ShouldNotThrow_ExceptArgumentGuards() =>
        ArchitectureRules.DomainThrowsOnlyArgumentGuards(Map, AllowedThrowingTypes);
}
