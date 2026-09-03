using System.Globalization;
using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// SLO alert-to-runbook pairing gate (rubric section 13): every SLO metric alert provisioned by the
/// consumer's <c>infra/main.bicep</c> (<c>sloAlertSpecs</c>) must keep a matching triage section in
/// its <c>infra/OPERATIONS.md</c>, and that section's heading must carry the alert's current
/// severity, so an alert cannot be added, renamed, or re-tiered without its runbook moving in the
/// same change. The reverse direction is guarded too: an orphan runbook section whose alert no
/// longer exists fails the build. A minimum-spec floor keeps the gate non-vacuous, so a drifted
/// parse anchor fails loudly instead of passing with zero discovered alerts.
/// <para>
/// <b>Subclassing:</b> both files must be embedded as manifest resources in the SUBCLASS's test
/// project, with the logical names <c>infra.main.bicep</c> and <c>infra.OPERATIONS.md</c>:
/// </para>
/// <code>
/// &lt;EmbeddedResource Include="..\..\..\infra\main.bicep"&gt;
///   &lt;LogicalName&gt;infra.main.bicep&lt;/LogicalName&gt;
/// &lt;/EmbeddedResource&gt;
/// </code>
/// <para>
/// Resources are read from <see cref="ResourceAssembly"/>, which defaults to the DERIVED type's
/// assembly. That default is load-bearing: this base ships inside the framework package, so
/// resolving against its own assembly would look for the consumer's bicep inside
/// MMCA.Common.Testing.Architecture.dll and always throw.
/// </para>
/// </summary>
public abstract partial class ObservabilityConventionTestsBase
{
    private const string AlertNameInfix = "-alert-";

    /// <summary>
    /// Lowest number of SLO alert specs the consumer's bicep is expected to declare. The floor is
    /// what keeps this gate honest: discovering fewer means the parse anchors drifted, not that the
    /// alerts silently disappeared. Raise it in the subclass as the consumer provisions more.
    /// </summary>
    protected virtual int MinimumAlertSpecs => 3;

    /// <summary>Manifest-resource logical name of the consumer's IaC template.</summary>
    protected virtual string BicepResource => "infra.main.bicep";

    /// <summary>Manifest-resource logical name of the consumer's operations runbook.</summary>
    protected virtual string RunbookResource => "infra.OPERATIONS.md";

    /// <summary>
    /// Assembly the embedded resources are read from. Defaults to the derived type's assembly, so a
    /// subclass gets its OWN test project's resources with no extra wiring.
    /// </summary>
    protected virtual Assembly ResourceAssembly => GetType().Assembly;

    [Fact]
    public void SloAlertSpecs_AreDiscovered_GateIsNotVacuous()
    {
        var specs = DiscoverAlertSpecs();

        specs.Should().HaveCountGreaterThanOrEqualTo(
            MinimumAlertSpecs,
            because: $"infra/main.bicep provisions at least {MinimumAlertSpecs.ToString(CultureInfo.InvariantCulture)} SLO alerts; discovering fewer means the sloAlertSpecs parse anchors drifted and the pairing gate would pass vacuously");
    }

    [Fact]
    public void EveryProvisionedSloAlert_HasASeverityCorrectRunbookSection()
    {
        var specs = DiscoverAlertSpecs();
        var runbook = ReadEmbedded(RunbookResource);
        var headings = DiscoverRunbookAlertHeadings(runbook);

        var violations = new List<string>();
        foreach (var (key, severity) in specs)
        {
            var heading = headings.Find(h => h.Contains(AlertNameInfix + key, StringComparison.Ordinal));
            if (heading is null)
            {
                violations.Add($"alert '{key}' has no '### ...{AlertNameInfix}{key}' runbook section in infra/OPERATIONS.md");
                continue;
            }

            var severityTag = string.Create(CultureInfo.InvariantCulture, $"(sev {severity})");
            if (!heading.Contains(severityTag, StringComparison.Ordinal))
            {
                violations.Add(string.Create(CultureInfo.InvariantCulture, $"alert '{key}' is severity {severity} in main.bicep, but its runbook heading does not carry '{severityTag}': {heading.Trim()}"));
            }
        }

        violations.Should().BeEmpty(
            because: "every provisioned SLO alert must keep a severity-correct triage section in infra/OPERATIONS.md, so alerts and runbooks change together (rubric section 13)");
    }

    [Fact]
    public void EveryRunbookAlertSection_MapsToAProvisionedAlert()
    {
        var specKeys = DiscoverAlertSpecs().ConvertAll(s => s.Key);
        var runbook = ReadEmbedded(RunbookResource);

        var orphans = DiscoverRunbookAlertHeadings(runbook)
            .Where(heading => !specKeys.Exists(key => heading.Contains(AlertNameInfix + key, StringComparison.Ordinal)))
            .ToList();

        orphans.Should().BeEmpty(
            because: "a runbook section for an alert that main.bicep no longer provisions is stale guidance; remove or rename it in the same change as the alert (rubric section 13)");
    }

    private List<(string Key, int Severity)> DiscoverAlertSpecs()
    {
        var bicep = ReadEmbedded(BicepResource);

        var start = bicep.IndexOf("var sloAlertSpecs", StringComparison.Ordinal);
        var end = bicep.IndexOf("resource sloAlerts", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, because: "infra/main.bicep must declare the sloAlertSpecs variable this gate parses");
        end.Should().BeGreaterThan(start, because: "infra/main.bicep must materialize sloAlertSpecs into the sloAlerts resource after declaring it");

        var block = bicep[start..end];
        var keys = AlertKeyRegex.Matches(block);
        var severities = AlertSeverityRegex.Matches(block);
        keys.Count.Should().Be(severities.Count, because: "every sloAlertSpecs entry declares exactly one key and one severity, so a count mismatch means the spec shape changed and this parser must follow");

        var specs = new List<(string Key, int Severity)>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            specs.Add((keys[i].Groups["key"].Value, int.Parse(severities[i].Groups["sev"].Value, CultureInfo.InvariantCulture)));
        }

        return specs;
    }

    private static List<string> DiscoverRunbookAlertHeadings(string runbook) =>
        [.. RunbookHeadingRegex.Matches(runbook).Select(m => m.Value).Where(h => h.Contains(AlertNameInfix, StringComparison.Ordinal))];

    private string ReadEmbedded(string logicalName)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} must be embedded as a resource in {ResourceAssembly.GetName().Name} for the alert-to-runbook pairing gate to run");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"key:\s*'(?<key>[a-z0-9-]+)'", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AlertKeyRegex { get; }

    [GeneratedRegex(@"severity:\s*(?<sev>\d)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AlertSeverityRegex { get; }

    [GeneratedRegex(@"^###\s+.*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RunbookHeadingRegex { get; }
}
