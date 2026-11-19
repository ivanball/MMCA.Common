using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing.Conformance.ServiceDiscovery;

/// <summary>
/// Pins the service-discovery topology to ONE shape across the two places that declare it: the
/// Aspire AppHost (local dev, the E2E stack) and the Azure Container Apps Bicep template
/// (production).
/// <para>
/// Aspire's <c>WithReference(peer)</c> injects <c>services__{peer}__{endpoint}__0</c> environment
/// variables, and Bicep hand-writes the same variables onto each container app. Nothing connects the
/// two, so a new cross-service call wired in the AppHost boots locally, passes every test, and then
/// fails in production with an unresolvable service name (and the reverse: a Bicep entry for an edge
/// that no longer exists is dead configuration nobody notices). This base is the connection.
/// </para>
/// <para>
/// Both files are read as text (see <c>AppHostBicepEdgeParser</c>), never by booting the AppHost.
/// An edge is <c>"consumer-&gt;peer"</c> in Aspire resource names: an AppHost edge comes from
/// <c>WithReference</c> between two <c>AddDotnetProject</c> resources (chained onto the declaration or on
/// a later statement) and from every resource argument of <c>WithMmcaGateway</c> and
/// <c>WithMmcaUi</c>; <c>WithJwksDiscovery</c> is not an edge. A Bicep edge is a
/// <c>services__{peer}__...</c> entry inside the container app named <c>'${prefix}-{consumer}'</c>.
/// </para>
/// <para>
/// Subclass it once per application with <see cref="AppHostProgramPath"/> and, for every edge
/// production deliberately does not wire, a <see cref="KnownAppHostOnlyEdges"/> entry with its reason.
/// </para>
/// </summary>
public abstract class AppHostBicepParityTestsBase
{
    /// <summary>
    /// Gets the AppHost <c>Program.cs</c> path, relative to <see cref="RepositoryRoot"/>
    /// (for example <c>Source/Hosting/MyApp.AppHost/Program.cs</c>).
    /// </summary>
    protected abstract string AppHostProgramPath { get; }

    /// <summary>Gets the Bicep template path, relative to <see cref="RepositoryRoot"/>. Defaults to <c>infra/main.bicep</c>.</summary>
    protected virtual string BicepPath => Path.Combine("infra", "main.bicep");

    /// <summary>
    /// Gets the edges the AppHost declares that production deliberately does NOT wire as service
    /// discovery, each mapped to the reason. An unexplained entry is indistinguishable from the drift
    /// this test exists to catch, so a blank reason fails the test. Defaults to none.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> KnownAppHostOnlyEdges { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the repository root both paths are relative to. Defaults to the nearest directory above
    /// the test binaries that contains <see cref="AppHostProgramPath"/>.
    /// </summary>
    protected virtual string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, AppHostProgramPath)))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException(
                $"No directory above '{AppContext.BaseDirectory}' contains '{AppHostProgramPath}'; the test must run from inside the repository.");
        }
    }

    /// <summary>
    /// Fails when an AppHost edge has no <c>services__</c> entry on the matching container app and is
    /// not a listed <see cref="KnownAppHostOnlyEdges"/> entry.
    /// </summary>
    [Fact]
    public void EveryAppHostServiceReference_HasAMatchingBicepServiceDiscoveryEntry()
    {
        // Arrange
        var appHostEdges = ReadAppHostEdges();
        var bicepEdges = ReadBicepEdges();
        var known = KnownAppHostOnlyEdges;

        // Act
        var missingInBicep = appHostEdges.Except(bicepEdges, StringComparer.Ordinal)
            .Except(known.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Assert
        known.Where(entry => string.IsNullOrWhiteSpace(entry.Value)).Select(entry => entry.Key).Should().BeEmpty(
            "every known AppHost-only edge needs a reason, or it cannot be told apart from drift");
        appHostEdges.Should().NotBeEmpty("the AppHost wires cross-service references; an empty parse means this test stopped reading the file");
        missingInBicep.Should().BeEmpty(
            "every AppHost WithReference edge must have a services__<peer>__<endpoint>__0 entry on the matching container app in the Bicep template, "
            + "or the call works locally and fails in production with an unresolvable service name");
    }

    /// <summary>Fails when a Bicep <c>services__</c> entry has no matching AppHost reference.</summary>
    [Fact]
    public void EveryBicepServiceDiscoveryEntry_HasAMatchingAppHostReference()
    {
        // Arrange
        var appHostEdges = ReadAppHostEdges();
        var bicepEdges = ReadBicepEdges();

        // Act
        var missingInAppHost = bicepEdges.Except(appHostEdges, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Assert
        bicepEdges.Should().NotBeEmpty("the Bicep template injects services__ entries; an empty parse means this test stopped reading the file");
        missingInAppHost.Should().BeEmpty(
            "a services__ entry in the Bicep template with no matching AppHost WithReference is either dead production configuration "
            + "or an edge the local stack cannot exercise at all");
    }

    private HashSet<string> ReadAppHostEdges() =>
        AppHostBicepEdgeParser.ParseAppHostEdges(File.ReadAllText(Path.Combine(RepositoryRoot, AppHostProgramPath)));

    private HashSet<string> ReadBicepEdges() =>
        AppHostBicepEdgeParser.ParseBicepEdges(File.ReadAllLines(Path.Combine(RepositoryRoot, BicepPath)));
}
