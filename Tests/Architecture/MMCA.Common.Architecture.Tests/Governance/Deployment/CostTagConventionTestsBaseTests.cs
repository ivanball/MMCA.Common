using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance.Deployment;

/// <summary>
/// Cross-assembly guard for <see cref="CostTagConventionTestsBase"/>. This subclass lives outside the
/// package and points at a fixture template embedded here, so inheriting the two facts exercises the
/// discovery and tag checks across the assembly boundary, the way a consumer's subclass runs them. The
/// negative cases run private subclasses (not collected by xUnit) and assert the failure itself.
/// </summary>
public sealed class CostTagConventionTestsBaseTests : CostTagConventionTestsBase
{
    protected override int MinimumContainerApps => 2;

    protected override string DatabaseNamePrefix => "Fixture_";

    protected override string BicepResource => "fixtures.costtag-main.bicep";

    [Fact]
    public void ResourceAssembly_DefaultsToTheDerivedTypesAssembly() =>
        ResourceAssembly.Should().BeSameAs(typeof(CostTagConventionTestsBaseTests).Assembly);

    [Fact]
    public void UntaggedContainerApp_IsFlaggedByName()
    {
        var assert = new UntaggedTemplate().EveryContainerApp_CarriesAServiceTag;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain("uiApp", "the failure must name the app whose spend is unattributed");
    }

    [Fact]
    public void TooFewContainerApps_FailsTheFloor()
    {
        var assert = new HighFloorTemplate().EveryContainerApp_CarriesAServiceTag;

        assert.Should().Throw<Exception>(
            "discovering fewer apps than the floor means the parse anchor drifted, so the gate must not pass vacuously");
    }

    [Fact]
    public void DatabaseTag_UnderAnotherPrefix_IsFlagged()
    {
        var assert = new WrongPrefixTemplate().ServiceDatabases_CarryTheServiceTagOfTheOwningApp;

        assert.Should().Throw<Exception>(
            "the database tag must strip the repo's own prefix, so a template deriving it from another prefix fails");
    }

    private sealed class UntaggedTemplate : CostTagConventionTestsBase
    {
        protected override int MinimumContainerApps => 2;

        protected override string DatabaseNamePrefix => "Fixture_";

        protected override string BicepResource => "fixtures.costtag-untagged.bicep";
    }

    private sealed class HighFloorTemplate : CostTagConventionTestsBase
    {
        protected override int MinimumContainerApps => 3;

        protected override string DatabaseNamePrefix => "Fixture_";

        protected override string BicepResource => "fixtures.costtag-main.bicep";
    }

    private sealed class WrongPrefixTemplate : CostTagConventionTestsBase
    {
        protected override int MinimumContainerApps => 2;

        protected override string DatabaseNamePrefix => "Other_";

        protected override string BicepResource => "fixtures.costtag-main.bicep";
    }
}
