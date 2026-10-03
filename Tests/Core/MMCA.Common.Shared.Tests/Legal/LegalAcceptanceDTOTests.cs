using AwesomeAssertions;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.Shared.Tests.Legal;

/// <summary>
/// Pins <see cref="LegalAcceptanceDTO.Evaluate"/>: with no current version every user is current (the
/// feature-off answer never blocks anyone), and otherwise only an accepted version equal to the
/// current one counts.
/// </summary>
public sealed class LegalAcceptanceDTOTests
{
    private static readonly DateTime AcceptedOn = new(2026, 9, 30, 8, 15, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_WithNoCurrentVersion_IsCurrent(string? currentVersion)
    {
        var standing = LegalAcceptanceDTO.Evaluate(currentVersion, acceptedVersion: null, acceptedOn: null);

        standing.CurrentVersion.Should().BeNull("an unset version is the feature-off answer");
        standing.IsCurrent.Should().BeTrue("a host that never opted in must not block anyone");
    }

    [Fact]
    public void Evaluate_WhenTheAcceptedVersionEqualsTheCurrentOne_IsCurrent()
    {
        var standing = LegalAcceptanceDTO.Evaluate("2026-10-01", "2026-10-01", AcceptedOn);

        standing.IsCurrent.Should().BeTrue();
        standing.CurrentVersion.Should().Be("2026-10-01");
        standing.AcceptedVersion.Should().Be("2026-10-01");
        standing.AcceptedOn.Should().Be(AcceptedOn);
    }

    [Fact]
    public void Evaluate_WhenNothingWasEverAccepted_IsNotCurrent()
    {
        var standing = LegalAcceptanceDTO.Evaluate("2026-10-01", acceptedVersion: null, acceptedOn: null);

        standing.IsCurrent.Should().BeFalse();
        standing.AcceptedVersion.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WhenAnOlderVersionWasAccepted_IsNotCurrent()
    {
        var standing = LegalAcceptanceDTO.Evaluate("2026-10-01", "2026-01-01", AcceptedOn);

        standing.IsCurrent.Should().BeFalse("a version bump asks every user to accept again");
        standing.CurrentVersion.Should().Be("2026-10-01");
        standing.AcceptedVersion.Should().Be("2026-01-01");
    }
}
