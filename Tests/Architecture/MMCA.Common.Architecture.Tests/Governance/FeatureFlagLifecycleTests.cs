using MMCA.Common.Architecture.Tests.FeatureFlagFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Runs the shared feature-flag lifecycle rules (ADR-031) against MMCA.Common's own Shared assembly
/// through <see cref="FeatureFlagLifecycleTestsBase"/>, the same way MMCA.ADC and MMCA.Store subclass
/// it for their module <c>*Features</c> classes. Not vacuous: it really walks
/// <c>NotificationFeatures</c> and <c>PrivacyFeatures</c>, and the day the framework adds a flag
/// without declaring its lifecycle this build fails.
/// </summary>
public sealed class FeatureFlagLifecycleTests : FeatureFlagLifecycleTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}

/// <summary>
/// Adversarial coverage for the same two rules, pointed at THIS test assembly, whose fixtures declare
/// one flag per way of getting it wrong. A rule that only ever passes proves nothing: each failure
/// must name the offending flag.
/// </summary>
public sealed class FeatureFlagLifecycleRuleTests
{
    private static readonly DateOnly Today = new(2026, 9, 11);

    [Fact]
    public void DeclarationRule_FlagsAConstantWithNoAttribute() =>
        DeclarationFailure().Should().Contain(
            nameof(FixtureBadFeatures.UnannotatedFlag),
            "an undeclared flag is the case the whole gate exists for");

    [Fact]
    public void DeclarationRule_FlagsAPermanentFlagThatSetsRemoveBy()
    {
        var message = DeclarationFailure();

        message.Should().Contain(nameof(FixtureBadFeatures.PermanentWithRemoveByFlag));
        message.Should().Contain("is Permanent but sets RemoveBy");
    }

    [Fact]
    public void DeclarationRule_FlagsATemporaryFlagWithNoRemoveBy()
    {
        var message = DeclarationFailure();

        message.Should().Contain(nameof(FixtureBadFeatures.TemporaryWithoutRemoveByFlag));
        message.Should().Contain("must set RemoveBy");
    }

    [Fact]
    public void DeclarationRule_FlagsATemporaryFlagWhoseRemoveByDoesNotParse()
    {
        var message = DeclarationFailure();

        message.Should().Contain(nameof(FixtureBadFeatures.TemporaryWithUnparseableRemoveByFlag));
        message.Should().Contain("is not an ISO yyyy-MM-dd date");
    }

    [Fact]
    public void DeclarationRule_LeavesCorrectlyDeclaredFlagsAlone()
    {
        var message = DeclarationFailure();

        message.Should().NotContain(nameof(FixtureGoodFeatures.PermanentFlag));
        message.Should().NotContain(nameof(FixtureGoodFeatures.LiveTemporaryFlag));
    }

    [Fact]
    public void ExpiryRule_FlagsATemporaryFlagPastItsRemoveBy_NamingTheDateAndTheOwner()
    {
        var act = () => ArchitectureRules.TemporaryFeatureFlagsAreNotPastRemoveBy(new FixtureMap(), Today);

        var message = act.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(nameof(FixtureExpiredFeatures.ExpiredFlag));
        message.Should().Contain("2020-01-01", "the message must carry the date the toggle was due to go");
        message.Should().Contain("owner=Fixtures", "and someone to hand it to");
        message.Should().NotContain(
            nameof(FixtureGoodFeatures.LiveTemporaryFlag),
            "a temporary flag still inside its window is not a dead toggle");
    }

    [Fact]
    public void ExpiryRule_PassesOnTheDayOfTheRemoveByDate() =>
        FluentActions.Invoking(() => ArchitectureRules.TemporaryFeatureFlagsAreNotPastRemoveBy(
                new FixtureMap(), new DateOnly(2020, 1, 1)))
            .Should().NotThrow("the flag is due to be gone BY that date, so that date is still inside the window");

    [Fact]
    public void ExpiryRule_PassesOnTheFrameworksOwnFlags() =>
        FluentActions.Invoking(() => ArchitectureRules.TemporaryFeatureFlagsAreNotPastRemoveBy(
                new CommonArchitectureMap(), Today))
            .Should().NotThrow("every framework flag is Permanent, so none of them can expire");

    private static string DeclarationFailure()
    {
        var act = () => ArchitectureRules.FeatureFlagsDeclareLifetime(new FixtureMap());

        return act.Should().Throw<Exception>().Which.Message;
    }

    /// <summary>A Shared-layer map over this test assembly, so the rules see the deliberately bad fixtures.</summary>
    private sealed class FixtureMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Shared, typeof(FixtureBadFeatures).Assembly)];
    }
}
