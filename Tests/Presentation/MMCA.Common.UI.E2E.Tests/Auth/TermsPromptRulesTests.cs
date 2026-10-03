using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Auth;

/// <summary>
/// The rule behind <c>E2ETestBase.AcceptTermsIfPromptedAsync</c>: the probe expects the acceptance
/// dialog exactly when the gate would open it (a configured current version and a standing that is not
/// current), and answers "unknown" for a body that is not a legal-acceptance standing.
/// </summary>
public sealed class TermsPromptRulesTests
{
    [Theory]
    [InlineData("""{"currentVersion":"2026-10-03","acceptedVersion":null,"isCurrent":false}""", true)]
    [InlineData("""{"currentVersion":"2026-10-03","acceptedVersion":"2026-01-01","isCurrent":false}""", true)]
    [InlineData("""{"CurrentVersion":"2026-10-03","IsCurrent":false}""", true)]
    [InlineData("""{"currentVersion":"2026-10-03","acceptedVersion":"2026-10-03","isCurrent":true}""", false)]
    [InlineData("""{"currentVersion":null,"acceptedVersion":null,"isCurrent":true}""", false)]
    [InlineData("""{"currentVersion":null,"isCurrent":false}""", false)]
    [InlineData("""{"currentVersion":"   ","isCurrent":false}""", false)]
    public void Standing_PredictsTheGate(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);

        E2ETestBase.IsTermsPromptExpected(document.RootElement).Should().Be(
            expected,
            "the probe must agree with TermsAcceptanceGate, which opens only for a configured version that is not current");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{"currentVersion":"2026-10-03"}""")]
    [InlineData("""{"currentVersion":"2026-10-03","isCurrent":"false"}""")]
    public void NonStandingBody_IsUnknown(string json)
    {
        using var document = JsonDocument.Parse(json);

        E2ETestBase.IsTermsPromptExpected(document.RootElement).Should().BeNull(
            "a body that is not a legal-acceptance standing must fall back to waiting for the dialog");
    }
}
