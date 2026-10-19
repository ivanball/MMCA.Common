using MMCA.Common.Architecture.Tests.AnonymizationFixtures;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.Privacy;

/// <summary>
/// Self-test for the two reverse-PII rules behind <see cref="PiiConventionTestsBase"/>
/// (<c>EveryAnonymizableDomainEntity_IsCoveredBySample</c> and
/// <c>PropertiesOverwrittenByAnonymize_MustCarryPii</c>). Each rule must pass on a conforming input and
/// fail on a broken one, naming the offender in its message, so a consumer's red build says exactly
/// what to fix.
/// </summary>
public sealed class AnonymizationRulesTests
{
    // -- PropertiesOverwrittenByAnonymizeCarryPii --
    [Fact]
    public void ReverseRule_UnmarkedPropertyOverwrittenByAnonymize_IsReported()
    {
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(UnmarkedOverwriteSubject), static () => new UnmarkedOverwriteSubject())));

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("UnmarkedOverwriteSubject.Nickname", "Anonymize() overwrites it and it carries no [Pii]");
        message.Should().Contain("[Pii]", "the message is the instruction: it says what to mark");
    }

    [Fact]
    public void ReverseRule_IgnoresValueTypesCollectionsAndUntouchedProperties()
    {
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(UnmarkedOverwriteSubject), static () => new UnmarkedOverwriteSubject())));

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().NotContain("UnmarkedOverwriteSubject.IsErased", "a flag the erasure flips is not personal data");
        message.Should().NotContain("UnmarkedOverwriteSubject.Tags", "collections are out of the rule's scope");
        message.Should().NotContain("UnmarkedOverwriteSubject.City", "the erasure leaves it alone");
    }

    [Fact]
    public void ReverseRule_MarkedPropertyOverwrittenByAnonymize_IsNotReported()
    {
        // RefreshSession.Anonymize() nulls IpAddress and UserAgent, both [Pii]-marked.
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(RefreshSession), BuildRefreshSession)));

        act.Should().NotThrow();
    }

    [Fact]
    public void ReverseRule_MixedTable_ReportsOnlyTheUnmarkedOverwrite()
    {
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(RefreshSession), BuildRefreshSession),
            (typeof(UnmarkedOverwriteSubject), static () => new UnmarkedOverwriteSubject())));

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("UnmarkedOverwriteSubject.Nickname");
        message.Should().NotContain("RefreshSession.IpAddress");
        message.Should().NotContain("RefreshSession.UserAgent");
    }

    [Fact]
    public void ReverseRule_EmptyTable_Passes() =>
        FluentActions.Invoking(() => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples()))
            .Should().NotThrow("a repo with no anonymizable entity has nothing to check");

    [Fact]
    public void ReverseRule_AnonymizeThatFails_IsReported()
    {
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(FailingAnonymizeSubject), static () => new FailingAnonymizeSubject())));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("FailingAnonymizeSubject: Anonymize() did not succeed");
    }

    [Fact]
    public void ReverseRule_SampleKeyThatIsNotAnonymizable_IsReported()
    {
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(NotAnonymizableSubject), static () => new NotAnonymizableSubject())));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("NotAnonymizableSubject: the sample key does not implement");
    }

    [Fact]
    public void ReverseRule_FactoryReturningAnotherType_IsReported()
    {
        var act = () => ArchitectureRules.PropertiesOverwrittenByAnonymizeCarryPii(Samples(
            (typeof(UnmarkedOverwriteSubject), static () => new UnsampledSubject())));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("UnmarkedOverwriteSubject: the sample factory returned UnsampledSubject");
    }

    // -- AnonymizableEntitiesAreCoveredBySamples --
    [Fact]
    public void CoverageRule_AnonymizableEntityWithoutSample_IsReported()
    {
        Type[] candidates =
        [
            typeof(UnmarkedOverwriteSubject),
            typeof(UnsampledSubject),
            typeof(AbstractAnonymizableSubject),
            typeof(NotAnonymizableSubject),
        ];

        var act = () => ArchitectureRules.AnonymizableEntitiesAreCoveredBySamples(
            candidates,
            Samples((typeof(UnmarkedOverwriteSubject), static () => new UnmarkedOverwriteSubject())));

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(typeof(UnsampledSubject).FullName!);
        message.Should().Contain("AnonymizableSamples", "the message names the override to add the sample to");
        message.Should().NotContain(typeof(UnmarkedOverwriteSubject).FullName!, "it has a sample");
        message.Should().NotContain(nameof(AbstractAnonymizableSubject), "an abstract base is not an entity to sample");
        message.Should().NotContain(nameof(NotAnonymizableSubject), "it has no erasure path to check");
    }

    [Fact]
    public void CoverageRule_EveryConcreteAnonymizableSampled_Passes()
    {
        Type[] candidates =
        [
            typeof(UnmarkedOverwriteSubject),
            typeof(UnsampledSubject),
            typeof(AbstractAnonymizableSubject),
            typeof(NotAnonymizableSubject),
        ];

        var act = () => ArchitectureRules.AnonymizableEntitiesAreCoveredBySamples(
            candidates,
            Samples(
                (typeof(UnmarkedOverwriteSubject), static () => new UnmarkedOverwriteSubject()),
                (typeof(UnsampledSubject), static () => new UnsampledSubject())));

        act.Should().NotThrow();
    }

    [Fact]
    public void CoverageRule_NoAnonymizableEntityAndEmptyTable_Passes() =>
        FluentActions.Invoking(() => ArchitectureRules.AnonymizableEntitiesAreCoveredBySamples([typeof(NotAnonymizableSubject)], Samples()))
            .Should().NotThrow("the default empty table is correct for a repo with no anonymizable entity");

    private static RefreshSession BuildRefreshSession() =>
        RefreshSession.Create(
            userId: 1,
            refreshToken: "refresh-token",
            createdAt: new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc),
            expiresAt: new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc),
            ipAddress: "203.0.113.7",
            userAgent: "Mozilla/5.0 (fixture)").Value!;

    private static Dictionary<Type, Func<object>> Samples(params (Type Type, Func<object> Build)[] samples) =>
        samples.ToDictionary(static s => s.Type, static s => s.Build);
}
