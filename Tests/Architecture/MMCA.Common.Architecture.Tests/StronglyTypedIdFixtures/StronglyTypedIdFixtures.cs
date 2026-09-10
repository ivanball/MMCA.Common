using System.Diagnostics.CodeAnalysis;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Architecture.Tests.StronglyTypedIdFixtures;

/// <summary>
/// Compiled identifier shapes for <c>StronglyTypedIdFitnessTests</c>. The rule reads reflection
/// metadata that only the compiler can produce (the <c>IsReadOnly</c> attribute, the generated
/// <c>PrintMembers</c> that marks a record, the declared instance fields), so the only honest way to
/// test it is to compile the compliant and the offending shapes side by side and point a map at
/// this assembly.
/// <para>
/// Every fixture lives in this namespace and nowhere else, so it is invisible to the framework rules
/// that run over <c>CommonArchitectureMap</c> (the <c>Source/</c> assemblies, never this one).
/// </para>
/// </summary>
public readonly record struct CompliantFixtureId(int Value) : IStronglyTypedId<CompliantFixtureId, int>
{
    /// <summary>Wraps the primitive. The one member the canonical declaration writes.</summary>
    public static CompliantFixtureId From(int value) => new(value);
}

/// <summary>A second compliant shape, over a string, so the rule is not keyed to one primitive.</summary>
public readonly record struct CompliantStringFixtureId(string Value)
    : IStronglyTypedId<CompliantStringFixtureId, string>
{
    /// <summary>Wraps the primitive.</summary>
    public static CompliantStringFixtureId From(string value) => new(value);
}

/// <summary>
/// Not a record: hand-rolled, so it carries no compiler-generated structural equality. Two instances
/// wrapping the same key would compare by the default value-type equality rather than by contract,
/// which is the guarantee the framework's comparer and change tracking are written against.
/// </summary>
[SuppressMessage(
    "Performance",
    "CA1815:Override equals and operator equals on value types",
    Justification = "The MISSING equality is the point: this fixture exists so the fitness rule has a hand-rolled, non-record identifier to flag.")]
public readonly struct NotARecordFixtureId(int value) : IStronglyTypedId<NotARecordFixtureId, int>
{
    /// <inheritdoc />
    public int Value { get; } = value;

    /// <summary>Wraps the primitive.</summary>
    public static NotARecordFixtureId From(int value) => new(value);
}

/// <summary>
/// A record struct that is not <see langword="readonly"/>, so its wrapped value can be reassigned after an
/// entity has been keyed on it.
/// </summary>
public record struct MutableFixtureId(int Value) : IStronglyTypedId<MutableFixtureId, int>
{
    /// <summary>Wraps the primitive.</summary>
    public static MutableFixtureId From(int value) => new(value);
}

/// <summary>
/// A readonly record struct carrying a second value, which makes the column mapping ambiguous and
/// the converter's round trip lossy.
/// </summary>
public readonly record struct ExtraStateFixtureId(int Value, string Label)
    : IStronglyTypedId<ExtraStateFixtureId, int>
{
    /// <summary>Wraps the primitive, leaving the extra state at its default.</summary>
    public static ExtraStateFixtureId From(int value) => new(value, string.Empty);
}
