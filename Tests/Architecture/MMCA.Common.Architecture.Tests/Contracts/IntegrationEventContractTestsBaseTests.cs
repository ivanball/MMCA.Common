using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Contracts;

/// <summary>
/// Adversarial coverage for <see cref="IntegrationEventContractTestsBase"/>. The snapshot compares
/// each event's members as a SET, so a reordered declaration is not a breaking change and must not
/// fail the build; everything a consumer can actually observe (a missing member, an extra member, a
/// changed type) still must. The expected literals are derived from the live contract and then
/// mutated, so the cases stay honest as the fixture types change. The probe subclasses are private
/// so xUnit does not collect their inherited facts as tests of their own.
/// </summary>
public sealed class IntegrationEventContractTestsBaseTests
{
    [Fact]
    public void Base_Passes_WhenTheCommittedLiteralsAreAlphabetical()
    {
        // The shape every consumer's committed literal is written in today: whatever changes here,
        // an existing alphabetical snapshot must keep passing.
        var assert = new ProbeTests(LiveContract()).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().NotThrow();
    }

    [Fact]
    public void Base_Passes_WhenMembersAreListedInADifferentOrder()
    {
        var shuffled = LiveContract().Select(ReverseMembers).ToList();

        var assert = new ProbeTests(shuffled).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().NotThrow(
            "JSON carries no member order, so reordering two properties changes nothing a consumer can observe");
    }

    [Fact]
    public void Base_Fails_WhenTheCodeDeclaresAMemberTheContractDoesNot()
    {
        var expected = MutateFirstMultiMemberEvent(members => members[1..]);

        var assert = new ProbeTests(expected).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                "EXTRA member",
                "the committed contract is missing a member the code declares, which is a new property no consumer knows about");
    }

    [Fact]
    public void Base_Fails_WhenTheContractDeclaresAMemberTheCodeDropped()
    {
        var expected = MutateFirstMultiMemberEvent(members => [.. members, "GhostProperty:String"]);

        var assert = new ProbeTests(expected).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                "MISSING member GhostProperty:String",
                "a member the contract promises and the code no longer has breaks every consumer reading it");
    }

    [Fact]
    public void Base_Fails_WhenAMembersTypeChanges()
    {
        var expected = MutateFirstMultiMemberEvent(members =>
            [members[0][..(members[0].IndexOf(':', StringComparison.Ordinal) + 1)] + "Guid", .. members[1..]]);

        var assert = new ProbeTests(expected).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                "changed type",
                "a retyped property still deserializes, into the wrong shape, which is the worst kind of break");
    }

    [Fact]
    public void Base_Fails_WhenAnEventIsMissingFromTheSnapshot()
    {
        var assert = new ProbeTests([.. LiveContract().Skip(1)]).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain("NEW EVENT");
    }

    [Fact]
    public void Contract_SpellsOutGenericArguments_AndPinsAnIntermediateBase()
    {
        var contract = LiveContract();

        contract.Should().Contain(
            "MMCA.Common.Architecture.Tests.ContractShapeFixtures.IntegrationEvents.FixtureShapedEvent "
            + "{ Count:Nullable<Int32>, Origin:String, Tags:IReadOnlyList<String> }",
            "a retyped generic argument and a member of an intermediate consumer base both change the wire shape");
        contract.Where(l => l.Contains("ContractShapeFixtures", StringComparison.Ordinal))
            .Should().NotContain(
                l => l.Contains("SchemaVersion:", StringComparison.Ordinal) || l.Contains("DateOccurred:", StringComparison.Ordinal),
                "the framework envelope is the framework's contract, not the event's");
    }

    [Fact]
    public void Base_Fails_WhenAGenericArgumentOfAMultiArgumentMemberChanges()
    {
        var lines = LiveContract();
        var index = lines.FindIndex(l => l.Contains(".FixtureTallyEvent ", StringComparison.Ordinal));
        index.Should().BeGreaterThanOrEqualTo(0, "the fixture assembly must declare the multi-argument generic event");
        lines[index] = lines[index].Replace("<String, Int32>", "<String, Int64>", StringComparison.Ordinal);

        var assert = new ProbeTests(lines).IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                "member Counts changed type",
                "a comma inside angle brackets belongs to the member type, not the member list");
    }

    /// <summary>The contract the fixture map really produces, the baseline every case mutates.</summary>
    private static List<string> LiveContract() =>
        ArchitectureRules.BuildIntegrationEventContract(new FixtureMap());

    /// <summary>Rewrites one line with its members in reverse order, leaving the event name alone.</summary>
    private static string ReverseMembers(string line)
    {
        var (name, members) = Split(line);
        return $"{name} {{ {string.Join(", ", members.Reverse())} }}";
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the members of the first event that declares more than
    /// one, so a case exercises a real multi-member line rather than depending on a fixture's shape.
    /// </summary>
    private static List<string> MutateFirstMultiMemberEvent(Func<string[], string[]> mutate)
    {
        var lines = LiveContract();
        var index = lines.FindIndex(l => Split(l).Members.Length > 1);
        index.Should().BeGreaterThanOrEqualTo(0, "the fixture assembly must declare a multi-member integration event");

        var (name, members) = Split(lines[index]);
        lines[index] = $"{name} {{ {string.Join(", ", mutate(members))} }}";
        return lines;
    }

    private static (string Name, string[] Members) Split(string line)
    {
        var open = line.IndexOf('{', StringComparison.Ordinal);
        var close = line.LastIndexOf('}');
        return (line[..open].Trim(), SplitTopLevel(line[(open + 1)..close]));
    }

    /// <summary>Splits on the commas outside angle brackets, mirroring the base's own parser.</summary>
    private static string[] SplitTopLevel(string members)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < members.Length; i++)
        {
            depth += members[i] switch { '<' => 1, '>' => -1, _ => 0 };
            if (members[i] == ',' && depth == 0)
            {
                parts.Add(members[start..i]);
                start = i + 1;
            }
        }

        parts.Add(members[start..]);
        return [.. parts.Select(p => p.Trim()).Where(p => p.Length > 0)];
    }

    /// <summary>
    /// A consumer-shaped map over this test assembly: one module layer, so the scan covers the
    /// fixture integration events here and no framework-owned ones.
    /// </summary>
    private sealed class FixtureMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.FixtureRepo";

        protected override IEnumerable<LayerRef> DefineLayers()
        {
            yield return Module("Fixture", Layer.Shared, typeof(IntegrationEventContractTestsBaseTests).Assembly);
        }
    }

    private sealed class ProbeTests(IReadOnlyList<string> expected) : IntegrationEventContractTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap();

        protected override IReadOnlyList<string> ExpectedContract { get; } = expected;
    }
}
