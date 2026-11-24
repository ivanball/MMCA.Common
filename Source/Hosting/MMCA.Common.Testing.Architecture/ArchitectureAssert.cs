namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Shared assertion helpers for architecture fitness functions. One overload reports an
/// <see cref="ArchitectureRuleResult"/> from the dependency rules (<see cref="ArchitectureTypes"/>);
/// the other reports a reflection- or IL-derived violation list.
/// </summary>
public static class ArchitectureAssert
{
    /// <summary>Assert a dependency rule passed, listing the failing types on failure.</summary>
    public static void NoViolations(ArchitectureRuleResult result, string reason)
    {
        ArgumentNullException.ThrowIfNull(result);

        var violationList = string.Join(Environment.NewLine, result.FailingTypeNames.Select(name => $"  - {name}"));

        result.IsSuccessful.Should().BeTrue(
            because: $"{reason}. Violations:{Environment.NewLine}{violationList}");
    }

    /// <summary>Assert a reflection-derived violation list is empty, listing the violations on failure.</summary>
    public static void NoViolations(IEnumerable<string> violations, string reason)
    {
        var list = violations.ToList();

        list.Should().BeEmpty(
            because: $"{reason}.{Environment.NewLine}{string.Join(Environment.NewLine, list)}");
    }
}
