using AwesomeAssertions;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.Application.Tests.Auth.Legal;

/// <summary>
/// Pins the three rules of the Terms of Service acceptance held by
/// <see cref="LegalAcceptancePolicy"/>: an unset or blank version turns the feature off, only the
/// exact (ordinal, trimmed) current version can be accepted, and a consumer's answer is re-derived
/// rather than trusted.
/// </summary>
public sealed class LegalAcceptancePolicyTests
{
    // ── ResolveCurrentVersion ──
    [Fact]
    public void ResolveCurrentVersion_WithNullOptions_ReturnsNull() =>
        LegalAcceptancePolicy.ResolveCurrentVersion(null).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveCurrentVersion_WithNoUsableValue_ReturnsNull(string? configured) =>
        LegalAcceptancePolicy.ResolveCurrentVersion(new LegalAcceptanceOptions { CurrentTermsVersion = configured })
            .Should().BeNull("null, empty and whitespace are all the off switch");

    [Fact]
    public void ResolveCurrentVersion_TrimsTheConfiguredValue() =>
        LegalAcceptancePolicy.ResolveCurrentVersion(new LegalAcceptanceOptions { CurrentTermsVersion = " v1 " })
            .Should().Be("v1");

    // ── EnsureAcceptsCurrentVersion ──
    [Fact]
    public void EnsureAcceptsCurrentVersion_WhenTheSuppliedVersionIsTheCurrentOne_Succeeds() =>
        LegalAcceptancePolicy.EnsureAcceptsCurrentVersion("v1", "v1").IsSuccess.Should().BeTrue();

    [Fact]
    public void EnsureAcceptsCurrentVersion_TrimsTheSuppliedVersionBeforeComparing() =>
        LegalAcceptancePolicy.EnsureAcceptsCurrentVersion("v1", "  v1 ").IsSuccess.Should().BeTrue();

    [Fact]
    public void EnsureAcceptsCurrentVersion_WhenNoVersionIsConfigured_FailsWithVersionNotCurrent() =>
        AssertVersionNotCurrent(LegalAcceptancePolicy.EnsureAcceptsCurrentVersion(null, "v1"));

    [Fact]
    public void EnsureAcceptsCurrentVersion_WhenNoVersionIsSupplied_FailsWithVersionNotCurrent() =>
        AssertVersionNotCurrent(LegalAcceptancePolicy.EnsureAcceptsCurrentVersion("v1", null));

    [Fact]
    public void EnsureAcceptsCurrentVersion_WhenADifferentVersionIsSupplied_FailsWithVersionNotCurrent() =>
        AssertVersionNotCurrent(LegalAcceptancePolicy.EnsureAcceptsCurrentVersion("v2", "v1"));

    [Fact]
    public void EnsureAcceptsCurrentVersion_WhenOnlyTheCaseDiffers_FailsWithVersionNotCurrent() =>
        AssertVersionNotCurrent(LegalAcceptancePolicy.EnsureAcceptsCurrentVersion("v1", "V1"));

    // ── Normalize ──
    [Fact]
    public void Normalize_IgnoresAConsumerSuppliedCurrentVersionAndIsCurrent()
    {
        var acceptedOn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var wrong = new LegalAcceptanceDTO
        {
            CurrentVersion = "something-else",
            AcceptedVersion = "v1",
            AcceptedOn = acceptedOn,
            IsCurrent = true,
        };

        var normalized = LegalAcceptancePolicy.Normalize("v2", wrong);

        normalized.CurrentVersion.Should().Be("v2", "the configured version wins over the consumer's");
        normalized.IsCurrent.Should().BeFalse("v1 is not v2, whatever the consumer claimed");
        normalized.AcceptedVersion.Should().Be("v1");
        normalized.AcceptedOn.Should().Be(acceptedOn);
    }

    [Fact]
    public void Normalize_DerivesIsCurrentTrueEvenWhenTheConsumerSaidFalse()
    {
        var wrong = new LegalAcceptanceDTO { AcceptedVersion = "v2", IsCurrent = false };

        var normalized = LegalAcceptancePolicy.Normalize("v2", wrong);

        normalized.IsCurrent.Should().BeTrue();
        normalized.CurrentVersion.Should().Be("v2");
    }

    private static void AssertVersionNotCurrent(Result result)
    {
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e =>
            e.Code == LegalAcceptanceErrorCodes.VersionNotCurrent && e.Type == ErrorType.Validation);
    }
}
