using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// FinOps attribution gate (rubric section 34, cost visibility): every container app the consumer's
/// <c>infra/main.bicep</c> declares must carry a per-service <c>service</c> tag on top of the shared
/// <c>commonTags</c>, and every per-service database must carry the same dimension, so Azure Cost
/// Analysis can split the bill by service instead of stopping at the application total. A new app
/// added with plain <c>tags: commonTags</c> would silently fold its spend into an unattributed bucket,
/// with nothing else failing. A minimum container-app floor keeps the gate non-vacuous, so a drifted
/// parse anchor fails loudly instead of passing with zero discovered apps.
/// <para>
/// <b>Subclassing:</b> embed the template as a manifest resource in the SUBCLASS's test project with
/// the logical name <c>infra.main.bicep</c> (the same resource
/// <see cref="ObservabilityConventionTestsBase"/> reads), then supply
/// <see cref="MinimumContainerApps"/> and <see cref="DatabaseNamePrefix"/>:
/// </para>
/// <code>
/// &lt;EmbeddedResource Include="..\..\..\infra\main.bicep"&gt;
///   &lt;LogicalName&gt;infra.main.bicep&lt;/LogicalName&gt;
/// &lt;/EmbeddedResource&gt;
/// </code>
/// <para>
/// Resources are read from <see cref="ResourceAssembly"/>, which defaults to the DERIVED type's
/// assembly, so the base finds the consumer's template rather than looking inside this package.
/// </para>
/// </summary>
public abstract partial class CostTagConventionTestsBase
{
    /// <summary>
    /// Lowest number of <c>Microsoft.App/containerApps</c> resources the template is expected to
    /// declare (for example every service plus the gateway and the UI host). Discovering fewer means
    /// the parse anchor drifted, not that the apps disappeared.
    /// </summary>
    protected abstract int MinimumContainerApps { get; }

    /// <summary>
    /// The prefix the template strips from each database name to derive its <c>service</c> tag value
    /// (for example <c>ADC_</c> for <c>ADC_Conference</c>).
    /// </summary>
    protected abstract string DatabaseNamePrefix { get; }

    /// <summary>Manifest-resource logical name of the consumer's IaC template.</summary>
    protected virtual string BicepResource => "infra.main.bicep";

    /// <summary>
    /// Assembly the embedded template is read from. Defaults to the derived type's assembly, so a
    /// subclass gets its OWN test project's resource with no extra wiring.
    /// </summary>
    protected virtual Assembly ResourceAssembly => GetType().Assembly;

    /// <summary>
    /// Every container app declares a <c>tags:</c> line that unions a <c>service</c> key into the
    /// shared tags, within the first lines of its resource body.
    /// </summary>
    [Fact]
    public void EveryContainerApp_CarriesAServiceTag()
    {
        var bicep = ReadEmbedded(BicepResource);

        var apps = ContainerAppDeclaration.Matches(bicep);
        apps.Count.Should().BeGreaterThanOrEqualTo(
            MinimumContainerApps,
            because: "main.bicep must declare every deployable as a Microsoft.App/containerApps resource, so the tag check is not vacuous");

        var untagged = apps
            .Where(static m => !m.Groups["tags"].Value.Contains("service:", StringComparison.Ordinal))
            .Select(static m => m.Groups["name"].Value)
            .ToArray();

        untagged.Should().BeEmpty(
            because: "each container app must add a `service` tag to commonTags through a union with a service key, so its cost is attributable per service (untagged: "
                + string.Join(", ", untagged) + ")");
    }

    /// <summary>
    /// Each per-service database carries the same <c>service</c> dimension as the container app that
    /// owns it, derived from the database name with <see cref="DatabaseNamePrefix"/> removed.
    /// </summary>
    [Fact]
    public void ServiceDatabases_CarryTheServiceTagOfTheOwningApp()
    {
        DatabaseNamePrefix.Should().NotBeNullOrWhiteSpace(
            because: "the database tag is derived by stripping the repo's database-name prefix, so the prefix must be named");

        var bicep = ReadEmbedded(BicepResource);
        var expected = $"tags: union(commonTags, {{ service: toLower(replace(dbName, '{DatabaseNamePrefix}', '')) }})";

        bicep.Should().Contain(
            expected,
            because: "each per-service database carries the same service dimension as the container app that owns it, so one cost report covers compute and storage per service");
    }

    private string ReadEmbedded(string logicalName)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} must be embedded as a resource in {ResourceAssembly.GetName().Name} for the cost-tag gate to run");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // A container-app resource header, then up to three body lines before its tags: line (name and
    // location normally sit between them). A resource with no tags: line in that window still
    // matches, with an empty tags group, so it is reported as untagged rather than skipped.
    [GeneratedRegex(@"^resource (?<name>\w+) 'Microsoft\.App/containerApps@[^']+' = \{\r?\n(?:(?:[^\r\n]*\r?\n){0,3}?\s*(?<tags>tags:[^\r\n]*))?", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ContainerAppDeclaration { get; }
}
