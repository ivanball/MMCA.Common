using System.Collections.Frozen;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Liskov substitutability fitness function: no override of a base-class virtual or abstract member,
/// and no interface member implementation, throws <c>System.NotSupportedException</c> or
/// <c>System.NotImplementedException</c>. A subtype that refuses an operation its base or interface
/// promises breaks every caller that substitutes it in, and only at run time.
/// <para>
/// The rule reads IL (the same <see langword="throw"/> scan as <see cref="DomainThrowTestsBase"/>),
/// so only an exception constructed in place and thrown is judged; a method that overrides nothing
/// and implements nothing may throw what it likes. A consumer map scans its module assemblies only.
/// </para>
/// <para>
/// Adoption in a repo with existing refusals: subclass, run once, and either implement the member,
/// narrow the abstraction so the type no longer promises it, or list it in
/// <see cref="ExcludedMembers"/> with a comment saying why refusing is right there.
/// </para>
/// </summary>
public abstract class SubstitutabilityTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Fully-qualified members (<c>Namespace.Type.Method</c>; a nested type uses the reflection
    /// <c>Outer+Inner</c> spelling, a property accessor its <c>get_</c>/<c>set_</c> method name) whose
    /// refusal is the deliberate, reviewed choice, such as a write-only stream's <c>Read</c>. Empty by
    /// default.
    /// </summary>
    protected virtual IReadOnlySet<string> ExcludedMembers => FrozenSet<string>.Empty;

    [Fact]
    public void OverridesAndInterfaceImplementations_ShouldNotThrow_NotSupportedOrNotImplemented() =>
        ArchitectureRules.SubstitutableMembersDoNotRefuseTheContract(Map, ExcludedMembers);
}
