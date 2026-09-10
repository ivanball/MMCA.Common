using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Preconditions;

namespace MMCA.Common.Testing.Aspire.Tests.Preconditions;

/// <summary>
/// The gate decides whether an AppHost-backed collection runs at all, so its arithmetic is worth
/// proving without an AppHost: every machine fact arrives as a delegate.
/// </summary>
public sealed class AppHostEnvironmentGateTests
{
    private static string? Read(string name) => null;

    [Fact]
    public void NoRequirements_AlwaysRuns() =>
        AppHostEnvironmentGate
            .Evaluate(AppHostEnvironmentRequirement.None, Read, () => false, () => false)
            .Should().BeNull("a fixture that needs nothing must never be skipped");

    [Fact]
    public void OptIn_SkipsWhenTheVariableIsAbsent()
    {
        var reason = AppHostEnvironmentGate.Evaluate(
            AppHostEnvironmentRequirement.OptIn,
            Read,
            () => true,
            () => true);

        reason.Should().Contain(AppHostEnvironmentGate.OptInVariable, "the skip reason must name the variable to set");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void OptIn_RunsForAnAffirmativeValue(string value) =>
        AppHostEnvironmentGate
            .Evaluate(AppHostEnvironmentRequirement.OptIn, _ => value, () => true, () => true)
            .Should().BeNull();

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    public void OptIn_SkipsForAnythingElse(string value) =>
        AppHostEnvironmentGate
            .Evaluate(AppHostEnvironmentRequirement.OptIn, _ => value, () => true, () => true)
            .Should().NotBeNull("only 1 and true opt in, so a typo cannot silently start an orchestrator");

    [Fact]
    public void Docker_SkipsWhenNoRuntimeIsReachable()
    {
        var reason = AppHostEnvironmentGate.Evaluate(
            AppHostEnvironmentRequirement.Docker,
            Read,
            () => false,
            () => true);

        reason.Should().Contain("container runtime");
    }

    [Fact]
    public void DeveloperCertificate_SkipsWhenAbsent_AndSaysHowToInstallIt()
    {
        var reason = AppHostEnvironmentGate.Evaluate(
            AppHostEnvironmentRequirement.DeveloperCertificate,
            Read,
            () => true,
            () => false);

        reason.Should().Contain("dev-certs", "a skip a human cannot act on is worse than a failure");
    }

    [Fact]
    public void OptIn_IsReportedBeforeDocker()
    {
        // Order matters for the developer experience: on a machine with neither, "these are opt-in"
        // is the actionable sentence and "no Docker" is noise.
        var reason = AppHostEnvironmentGate.Evaluate(
            AppHostEnvironmentRequirement.OptIn | AppHostEnvironmentRequirement.Docker,
            Read,
            () => false,
            () => false);

        reason.Should().Contain(AppHostEnvironmentGate.OptInVariable);
    }

    [Fact]
    public void EveryRequirementMet_Runs() =>
        AppHostEnvironmentGate
            .Evaluate(
                AppHostEnvironmentRequirement.OptIn | AppHostEnvironmentRequirement.Docker | AppHostEnvironmentRequirement.DeveloperCertificate,
                _ => "1",
                () => true,
                () => true)
            .Should().BeNull();
}
