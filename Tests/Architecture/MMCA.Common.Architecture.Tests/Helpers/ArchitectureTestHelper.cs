namespace MMCA.Common.Architecture.Tests.Helpers;

internal static class ArchitectureTestHelper
{
    internal static void AssertNoViolations(NetArchTest.Rules.TestResult result, string reason)
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
}
