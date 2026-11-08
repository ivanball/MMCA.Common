using AwesomeAssertions;
using MMCA.Common.Domain.FeatureFlags;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Domain.Tests.FeatureFlags;

/// <summary>
/// The invariants of one stored feature-flag override: a reason is required, a percentage is a
/// percentage, and a targeting override has someone to target.
/// </summary>
public sealed class FeatureFlagOverrideTests
{
    private const string Flag = "Sales.PaymentVerification";
    private const string Reason = "Provider outage, see incident 4711";

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutAReason_IsRefused(string reason)
    {
        var result = FeatureFlagOverride.Create(Flag, FeatureFlagOverrideMode.Off, reason);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlagOverride.ReasonRequired");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Create_APercentageOverrideOutsideZeroToOneHundred_IsRefused(int? percentage)
    {
        var result = FeatureFlagOverride.Create(Flag, FeatureFlagOverrideMode.Percentage, Reason, percentage);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlagOverride.PercentageInvalid");
    }

    [Fact]
    public void Create_ATargetingOverrideWithNobodyToTarget_IsRefused()
    {
        var result = FeatureFlagOverride.Create(Flag, FeatureFlagOverrideMode.Targeting, Reason, users: [" "], groups: []);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlagOverride.TargetingEmpty");
    }

    [Fact]
    public void Create_ATargetNameWithTheSeparator_IsRefused()
    {
        var result = FeatureFlagOverride.Create(Flag, FeatureFlagOverrideMode.Targeting, Reason, users: ["a,b"]);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("FeatureFlagOverride.TargetInvalid");
    }

    [Fact]
    public void Create_ATargetingOverride_KeepsItsTrimmedDistinctTargets()
    {
        var created = FeatureFlagOverride.Create(
            $" {Flag} ", FeatureFlagOverrideMode.Targeting, Reason, 10, users: ["alice", " alice ", "bob"], groups: ["Beta"]).Value!;

        created.Name.Should().Be(Flag);
        created.Users.Should().Equal("alice", "bob");
        created.Groups.Should().Equal("Beta");
        created.Percentage.Should().Be(10);
    }

    [Fact]
    public void Change_OnAClearedOverride_RevivesIt()
    {
        var entity = FeatureFlagOverride.Create(Flag, FeatureFlagOverrideMode.Off, Reason).Value!;
        entity.Clear().IsSuccess.Should().BeTrue();

        var changed = entity.Change(FeatureFlagOverrideMode.On, "Back on after the fix");

        changed.IsSuccess.Should().BeTrue();
        entity.IsDeleted.Should().BeFalse();
        entity.Mode.Should().Be(FeatureFlagOverrideMode.On);
    }

    [Fact]
    public void Change_ThatIsRefused_LeavesTheOverrideUnchanged()
    {
        var entity = FeatureFlagOverride.Create(Flag, FeatureFlagOverrideMode.Off, Reason).Value!;

        entity.Change(FeatureFlagOverrideMode.Percentage, "Ramp to a share", percentage: 300).IsFailure.Should().BeTrue();

        entity.Mode.Should().Be(FeatureFlagOverrideMode.Off);
        entity.Reason.Should().Be(Reason);
    }
}
