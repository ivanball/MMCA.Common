namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Shared assertion helpers for architecture fitness functions — the un-drifted successor to the
/// three per-repo <c>ArchitectureTestHelper.AssertNoViolations</c> copies. One overload reports a
/// NetArchTest <c>TestResult</c>; the other reports a reflection-derived violation list.
/// </summary>
public static class ArchitectureAssert
{
    /// <summary>Assert a NetArchTest result passed, listing the failing types on failure.</summary>
    public static void NoViolations(NetArchTest.Rules.TestResult result, string reason)
    {
        if (result.IsSuccessful)
        {
            return;
        }

        var failingTypes = result.FailingTypes ?? [];
        var violationList = string.Join(Environment.NewLine, failingTypes.Select(t => $"  - {t.FullName}"));

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
