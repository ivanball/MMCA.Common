namespace MMCA.Common.Architecture.Tests.SubstitutabilityFixtures;

/// <summary>
/// Compiled members for <c>SubstitutabilityRuleTests</c>. The rule reads IL, so the only honest way
/// to test it is to compile the refusals it must (and must not) flag into this assembly and point a
/// map at it: an override that refuses its base, an interface implementation that was never written,
/// a plain method that throws without promising anything, and an override the test excludes by name.
/// </summary>
internal abstract class FixtureShape
{
    internal abstract int Corners();

    internal virtual string Describe() => "shape";
}

/// <summary>Overrides a virtual member and refuses it: the rule must flag <c>Describe</c>.</summary>
internal sealed class FixtureRefusingShape : FixtureShape
{
    internal override int Corners() => 4;

    internal override string Describe() => throw new NotSupportedException("A refusing shape cannot describe itself.");
}

/// <summary>An interface the implementation below promises to honour.</summary>
internal interface IFixtureExporter
{
    string Export();
}

/// <summary>Implements an interface member with a placeholder: the rule must flag <c>Export</c>.</summary>
internal sealed class FixtureUnfinishedExporter : IFixtureExporter
{
    public string Export() => throw new NotImplementedException();
}

/// <summary>
/// Throws from a method that overrides nothing and implements nothing, so no caller was promised the
/// operation through a base type: the rule must leave it alone.
/// </summary>
internal static class FixtureStandaloneThrower
{
    internal static string Load(bool supported) =>
        supported ? "loaded" : throw new NotSupportedException("Only the supported path loads.");
}

/// <summary>
/// Refuses an abstract member exactly like <see cref="FixtureRefusingShape"/>, but the test lists
/// <c>Corners</c> as excluded, so the rule must stay silent about it.
/// </summary>
internal sealed class FixtureExcludedShape : FixtureShape
{
    internal override int Corners() => throw new NotSupportedException("Excluded by name in the test.");
}
