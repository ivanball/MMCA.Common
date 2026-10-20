using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Cross-assembly guard for <see cref="ObservabilityConventionTestsBase"/>. The base ships inside
/// the MMCA.Common.Testing.Architecture package but must read the embedded IaC resources of the
/// SUBCLASS's assembly. Resolving against the base's own assembly instead is a silent break: the
/// framework's own CI would stay green and the failure would only appear in the first consumer that
/// adopted it. This subclass lives in a different assembly from the base and points at fixture
/// resources embedded here, so inheriting the four [Fact]s exercises the whole discovery and
/// pairing path across the assembly boundary.
/// </summary>
public sealed class ObservabilityConventionTestsBaseTests : ObservabilityConventionTestsBase
{
    protected override string BicepResource => "fixtures.observability-main.bicep";

    protected override string RunbookResource => "fixtures.observability-OPERATIONS.md";

    /// <summary>
    /// The regression this file exists for: the default must resolve to the DERIVED type's
    /// assembly, not the assembly the base was compiled into.
    /// </summary>
    [Fact]
    public void ResourceAssembly_DefaultsToTheDerivedTypesAssembly()
    {
        ResourceAssembly.Should().BeSameAs(typeof(ObservabilityConventionTestsBaseTests).Assembly);
        ResourceAssembly.Should().NotBeSameAs(typeof(ObservabilityConventionTestsBase).Assembly);
    }

    /// <summary>
    /// L195: an alert key matches a heading only as a whole token, so with keys where one is a
    /// prefix of another the shorter key is not satisfied (or kept from being an orphan) by the
    /// longer key's section. Both pairing facts read headings through this one predicate. The one
    /// continuation allowed after the key is a version suffix (<c>-v2</c>), the shape ADC's
    /// provisioned alert names carry (<c>${prefix}-alert-${key}-v2</c>).
    /// </summary>
    [Theory]
    [InlineData("### `adc-prod-alert-failed-requests-v2` (sev 2): >10 failed HTTP requests / 15 min", "failed-requests", true)]
    [InlineData("### `<prefix>-alert-failed-requests` (sev 2): more than 10 failed HTTP requests in 15 minutes", "failed-requests", true)]
    [InlineData("### acc-alert-x-v12 (sev 1)", "x", true)]
    [InlineData("### acc-alert-x-v2", "x", true)]
    [InlineData("### acc-alert-x-extra-v2 (sev 1)", "x-extra", true)]
    [InlineData("### acc-alert-x-extra-v2 (sev 1)", "x", false)]
    [InlineData("### acc-alert-x-v2-extra (sev 1)", "x", false)]
    [InlineData("### acc-alert-x-v (sev 1)", "x", false)]
    [InlineData("### acc-alert-x-v2x (sev 1)", "x", false)]
    [InlineData("### acc-alert-x (sev 1)", "x-extra", false)]
    [InlineData("### `adc-prod-alert-failed-requests-v2` (sev 2)", "failed", false)]
    [InlineData("### acc-alert-server-response-time (sev 2)", "server-response-time", true)]
    [InlineData("### acc-alert-server-response-time", "server-response-time", true)]
    [InlineData("### acc-alert-server-response-time\r", "server-response-time", true)]
    [InlineData("### acc-alert-server-response-time(sev 2)", "server-response-time", true)]
    [InlineData("### `<prefix>-alert-server-response-time` (sev 2): slow", "server-response-time", true)]
    [InlineData("### `<prefix>-alert-server-response-time-p99` (sev 2): slow", "server-response-time", false)]
    [InlineData("### acc-alert-server-response-time-p99 (sev 2)", "server-response-time", false)]
    [InlineData("### acc-alert-x-extra (sev 1)", "x", false)]
    [InlineData("### acc-alert-x-extra and acc-alert-x (sev 1)", "x", true)]
    [InlineData("### acc-alert-server-response-time (sev 2)", "server-response-time-p99", false)]
    public void HeadingNamesAlert_MatchesTheKeyAsAWholeToken(string heading, string key, bool expected) =>
        HeadingNamesAlert(heading, key).Should().Be(expected);
}
