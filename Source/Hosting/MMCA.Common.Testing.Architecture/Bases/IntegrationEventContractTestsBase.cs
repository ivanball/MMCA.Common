namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Frozen wire-contract guard for the cross-service async API. A consumer in another service
/// deserializes integration events by shape, so a renamed/removed/retyped property — or a brand-new
/// event shipped without its consumer — silently breaks the contract. The subclass supplies the
/// committed <see cref="ExpectedContract"/>; this base rebuilds the live contract and compares. When a
/// change is intentional, version the event / coordinate the rollout and update ExpectedContract in the
/// same commit.
/// <para>
/// The member list inside each event's braces is compared as a SET, not a sequence. JSON carries no
/// member order, so reordering two properties in a record's declaration changes nothing a consumer
/// can observe; failing the build for it would train the one gate that guards real breakage to be
/// updated by rote. Everything that IS observable stays a failure: a missing member, an extra
/// member, a changed type, and any change to the set of events itself.
/// </para>
/// </summary>
public abstract class IntegrationEventContractTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>The committed snapshot: one line per integration event, "FullName { Prop:Type, ... }".</summary>
    protected abstract IReadOnlyList<string> ExpectedContract { get; }

    [Fact]
    public void IntegrationEventContracts_ShouldMatch_TheFrozenSnapshot()
    {
        var actual = ArchitectureRules.BuildIntegrationEventContract(Map);

        ArchitectureAssert.NoViolations(
            Compare(ExpectedContract, actual),
            "the integration-event wire contract changed. These events cross service boundaries over the "
            + "broker, so a renamed/removed/retyped property breaks consumers in other services. If "
            + "intentional, version the event / coordinate the consumer rollout, then update "
            + "ExpectedContract in this commit");
    }

    /// <summary>
    /// Reports every observable difference between the committed and the live contract: events only
    /// one side declares, and per-event members that are missing, extra, or retyped. Returns an
    /// empty list when the two agree, member order aside.
    /// </summary>
    /// <param name="expected">The committed snapshot lines.</param>
    /// <param name="actual">The lines rebuilt from the live types.</param>
    /// <returns>One human-readable line per difference.</returns>
    private static List<string> Compare(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var expectedEvents = Parse(expected);
        var actualEvents = Parse(actual);
        var differences = new List<string>();

        foreach (var name in expectedEvents.Keys.Where(n => !actualEvents.ContainsKey(n)).Order(StringComparer.Ordinal))
            differences.Add($"  - MISSING EVENT {name}: the committed contract declares it and the code no longer does");

        foreach (var name in actualEvents.Keys.Where(n => !expectedEvents.ContainsKey(n)).Order(StringComparer.Ordinal))
            differences.Add($"  - NEW EVENT {name}: shipped without a consumer rollout, or ExpectedContract was not updated");

        foreach (var name in expectedEvents.Keys.Where(actualEvents.ContainsKey).Order(StringComparer.Ordinal))
            differences.AddRange(CompareMembers(name, expectedEvents[name], actualEvents[name]));

        return differences;
    }

    /// <summary>
    /// Compares one event's member set. A member present on both sides under the same name but with a
    /// different type is reported as a retype rather than as an unrelated add and remove, because
    /// that is the failure a consumer sees: the property still deserializes, into the wrong shape.
    /// </summary>
    /// <param name="eventName">The event's full type name, for the message.</param>
    /// <param name="expected">The committed members, member name to declared type.</param>
    /// <param name="actual">The live members, member name to declared type.</param>
    /// <returns>One line per differing member.</returns>
    private static IEnumerable<string> CompareMembers(
        string eventName,
        Dictionary<string, string> expected,
        Dictionary<string, string> actual)
    {
        foreach (var (member, type) in expected.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(member, out var actualType))
            {
                yield return $"  - {eventName}: MISSING member {member}:{type}";
            }
            else if (!string.Equals(actualType, type, StringComparison.Ordinal))
            {
                yield return $"  - {eventName}: member {member} changed type {type} -> {actualType}";
            }
        }

        foreach (var (member, type) in actual.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!expected.ContainsKey(member))
            {
                yield return $"  - {eventName}: EXTRA member {member}:{type}";
            }
        }
    }

    /// <summary>
    /// Turns contract lines into event name to member map. A line that does not carry the
    /// <c>Name { members }</c> shape is kept whole as an event with no members, so a malformed
    /// committed literal surfaces as a mismatch rather than being silently dropped.
    /// </summary>
    /// <param name="lines">The contract lines to parse.</param>
    /// <returns>Each event's members, keyed by the event's full type name.</returns>
    private static Dictionary<string, Dictionary<string, string>> Parse(IReadOnlyList<string> lines)
    {
        var events = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var open = line.IndexOf('{', StringComparison.Ordinal);
            var close = line.LastIndexOf('}');
            if (open < 0 || close < open)
            {
                // Braces are stripped from the key: the reported difference is formatted through the
                // shared assertion helper, and a stray brace in a reason string is a format hazard.
                events[line.Replace("{", string.Empty, StringComparison.Ordinal)
                    .Replace("}", string.Empty, StringComparison.Ordinal)
                    .Trim()] = [];
                continue;
            }

            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in line[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = member.LastIndexOf(':');
                var (name, type) = separator < 0
                    ? (member, string.Empty)
                    : (member[..separator], member[(separator + 1)..]);
                members[name.Trim()] = type.Trim();
            }

            events[line[..open].Trim()] = members;
        }

        return events;
    }
}
