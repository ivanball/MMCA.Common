using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Shared.FeatureFlags;
using MMCA.Common.Shared.Notifications;
using MMCA.Common.Shared.Privacy;

namespace MMCA.Common.Shared.Tests.FeatureFlags;

/// <summary>
/// The runtime half of the feature-flag lifecycle contract (ADR-031): a host can read its own flag
/// inventory, with owner and removal date, without re-deriving the <c>*Features</c> convention. The
/// build-time half is the <c>FeatureFlagLifecycleTestsBase</c> fitness pair.
/// </summary>
public sealed class FeatureFlagRegistryTests
{
    private static readonly Assembly SharedAssembly = typeof(NotificationFeatures).Assembly;

    [Fact]
    public void Describe_FindsTheFrameworksOwnFlags()
    {
        var flags = FeatureFlagRegistry.Describe(SharedAssembly);

        flags.Select(f => f.FlagName).Should().Contain([
            NotificationFeatures.PushNotifications,
            PrivacyFeatures.DataExport]);
    }

    [Fact]
    public void Describe_CarriesTheDeclaredLifetimeOwnerAndFieldName()
    {
        var flag = FeatureFlagRegistry.Describe(SharedAssembly)
            .Single(f => string.Equals(f.FlagName, NotificationFeatures.PushNotifications, StringComparison.Ordinal));

        flag.FieldName.Should().Be(nameof(NotificationFeatures.PushNotifications));
        flag.Lifetime.Should().Be(FeatureFlagLifetime.Permanent);
        flag.Owner.Should().Be("MMCA.Common");
        flag.RemoveBy.Should().BeNull("a permanent flag must not carry a removal date");
    }

    [Fact]
    public void Describe_OrdersByFlagName()
    {
        var names = FeatureFlagRegistry.Describe(SharedAssembly).Select(f => f.FlagName).ToList();

        names.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Describe_ReportsAnUnannotatedFlagRatherThanHidingIt()
    {
        var flag = FeatureFlagRegistry.Describe(typeof(FeatureFlagRegistryTests).Assembly)
            .Single(f => string.Equals(f.FlagName, ProbeFeatures.Unannotated, StringComparison.Ordinal));

        flag.Lifetime.Should().BeNull("the fitness rule fails the build on it; the registry still lists it");
    }

    [Fact]
    public void Describe_IgnoresANonFeatureClassAndNonStringConstants()
    {
        var flags = FeatureFlagRegistry.Describe(typeof(FeatureFlagRegistryTests).Assembly);

        flags.Select(f => f.FieldName).Should().NotContain([
            nameof(ProbeFeatures.NotAFlag),
            nameof(ProbeSettings.LooksLikeAFlag)]);
    }

    [Fact]
    public void Describe_WithNullAssemblies_Throws()
    {
        var act = () => FeatureFlagRegistry.Describe((IEnumerable<Assembly>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("2026-12-31", true)]
    [InlineData("2026-2-3", false)]
    [InlineData("31/12/2026", false)]
    [InlineData(null, false)]
    public void TryParseRemoveBy_AcceptsIsoDatesOnly(string? raw, bool expected) =>
        FeatureFlagAttribute.TryParseRemoveBy(raw, out _).Should().Be(expected);

    /// <summary>A flag class in this test assembly, so the registry is exercised on an unannotated field too.</summary>
    internal static class ProbeFeatures
    {
        /// <summary>Deliberately carries no [FeatureFlag]: the registry reports it with a null lifetime.</summary>
        public const string Unannotated = "Probe.Unannotated";

        /// <summary>Not a string, so not a flag.</summary>
        public const int NotAFlag = 1;
    }

    /// <summary>A static class whose name does not end in "Features": its constants are not flags.</summary>
    internal static class ProbeSettings
    {
        /// <summary>A string constant outside a *Features class; the convention must skip it.</summary>
        public const string LooksLikeAFlag = "Probe.LooksLikeAFlag";
    }
}
