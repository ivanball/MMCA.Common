using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Architecture.Tests.FeatureFlagFixtures;

/// <summary>
/// Feature-flag constant classes that deliberately break each half of the lifecycle rule, so the
/// adversarial tests can prove the rule NAMES the offender instead of only ever passing. Nothing
/// points the framework's own <c>FeatureFlagLifecycleTests</c> at this assembly: it scans
/// MMCA.Common.Shared, where every flag is annotated.
/// </summary>
public static class FixtureGoodFeatures
{
    /// <summary>A permanent capability switch, correctly declared.</summary>
    [FeatureFlag(FeatureFlagLifetime.Permanent, Owner = "Fixtures")]
    public const string PermanentFlag = "Fixture.Permanent";

    /// <summary>A temporary rollout toggle whose date is far enough out to stay green.</summary>
    [FeatureFlag(FeatureFlagLifetime.Temporary, RemoveBy = "2099-12-31", Owner = "Fixtures")]
    public const string LiveTemporaryFlag = "Fixture.LiveTemporary";
}

/// <summary>Flags that violate the declaration rule, one violation per constant.</summary>
public static class FixtureBadFeatures
{
    /// <summary>Carries no <c>[FeatureFlag]</c> at all.</summary>
    public const string UnannotatedFlag = "Fixture.Unannotated";

    /// <summary>Permanent, yet names a removal date.</summary>
    [FeatureFlag(FeatureFlagLifetime.Permanent, RemoveBy = "2030-01-01", Owner = "Fixtures")]
    public const string PermanentWithRemoveByFlag = "Fixture.PermanentWithRemoveBy";

    /// <summary>Temporary with no removal date, which makes it permanent in everything but name.</summary>
    [FeatureFlag(FeatureFlagLifetime.Temporary, Owner = "Fixtures")]
    public const string TemporaryWithoutRemoveByFlag = "Fixture.TemporaryWithoutRemoveBy";

    /// <summary>Temporary with a removal date nobody can parse.</summary>
    [FeatureFlag(FeatureFlagLifetime.Temporary, RemoveBy = "31/12/2030", Owner = "Fixtures")]
    public const string TemporaryWithUnparseableRemoveByFlag = "Fixture.TemporaryWithUnparseableRemoveBy";
}

/// <summary>The dead toggle: temporary, correctly declared, and long past its date.</summary>
public static class FixtureExpiredFeatures
{
    /// <summary>Expired on 2020-01-01, which is what the expiry rule exists to catch.</summary>
    [FeatureFlag(FeatureFlagLifetime.Temporary, RemoveBy = "2020-01-01", Owner = "Fixtures")]
    public const string ExpiredFlag = "Fixture.Expired";
}
