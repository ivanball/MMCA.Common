using MMCA.Common.Architecture.Tests.PiiInventoryFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.Privacy;

/// <summary>
/// Self-test for <see cref="PiiInventoryTestsBase"/>. This subclass points the base at THIS test
/// assembly and the complete fixture inventory, so the two inherited facts prove a complete
/// inventory passes through the real file-reading path. The facts below run the same two rules over
/// the deliberately broken fixtures beside <see cref="FixturePiiSubject"/>: a missing row, a row with
/// an empty lawful basis, and an orphan row must each fail and name the member.
/// </summary>
public sealed class PiiInventoryTestsBaseTests : PiiInventoryTestsBase
{
    private static readonly IReadOnlyList<Assembly> FixtureAssemblies = [typeof(FixturePiiSubject).Assembly];

    protected override IReadOnlyList<Assembly> Assemblies => FixtureAssemblies;

    protected override string PrivacyPolicyPath => FixturePath("pii-inventory-complete.md");

    [Fact]
    public void MissingRow_Fails()
    {
        var act = () => ArchitectureRules.PiiMembersHaveInventoryRows(FixtureAssemblies, ReadFixture("pii-inventory-missing-row.md"));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("FixturePiiSubject.Phone has no row", "a [Pii] member the policy does not list is data held without a stated reason");
    }

    [Fact]
    public void RowWithEmptyLawfulBasis_Fails()
    {
        var act = () => ArchitectureRules.PiiMembersHaveInventoryRows(FixtureAssemblies, ReadFixture("pii-inventory-empty-basis.md"));

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain("FixturePiiSubject.Phone: the inventory row has an empty 'Lawful basis' cell");
        message.Should().NotContain("FixturePiiSubject.Email", "the complete row beside it is not a violation");
    }

    [Fact]
    public void OrphanRow_Fails()
    {
        var act = () => ArchitectureRules.PiiInventoryRowsMapToPiiMembers(FixtureAssemblies, ReadFixture("pii-inventory-orphan-row.md"));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("'FixturePiiSubject.Fax' names no live [Pii] member", "a row for an unmarked member means the policy and the code drifted apart");
    }

    [Fact]
    public void OrphanRowFixture_StillCoversEveryMember() =>
        FluentActions.Invoking(() => ArchitectureRules.PiiMembersHaveInventoryRows(FixtureAssemblies, ReadFixture("pii-inventory-orphan-row.md")))
            .Should().NotThrow("the orphan fixture lists every live member, so only the reverse direction may fail on it");

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "PiiInventoryFixtures", fileName);

    private static string ReadFixture(string fileName) => File.ReadAllText(FixturePath(fileName));
}
