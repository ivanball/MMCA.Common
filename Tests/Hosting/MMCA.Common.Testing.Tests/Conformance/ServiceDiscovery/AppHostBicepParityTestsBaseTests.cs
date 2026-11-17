using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Testing.Conformance.ServiceDiscovery;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance.ServiceDiscovery;

/// <summary>
/// Self-tests for <see cref="AppHostBicepParityTestsBase"/>: matching files pass both gates, a
/// missing or extra Bicep entry fails the matching gate, and the known-edge list must carry
/// reasons. The probe subclass is private, so xUnit never collects its inherited facts; the tests
/// call those facts directly against files written to a temporary repository root.
/// </summary>
public sealed class AppHostBicepParityTestsBaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mmca-parity-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Base_DeclaresBothGates_AsFacts()
    {
        var facts = typeof(AppHostBicepParityTestsBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name);

        facts.Should().BeEquivalentTo(
            [
                nameof(AppHostBicepParityTestsBase.EveryAppHostServiceReference_HasAMatchingBicepServiceDiscoveryEntry),
                nameof(AppHostBicepParityTestsBase.EveryBicepServiceDiscoveryEntry_HasAMatchingAppHostReference),
            ],
            "both directions run in each consumer's subclass; dropping one silently stops it running everywhere");
    }

    [Fact]
    public void MatchingFiles_WithEveryAppHostOnlyEdgeListed_PassBothGates()
    {
        var sut = WriteStoreShape(ServiceDiscoverySamples.StoreShapeBicepLines, ServiceDiscoverySamples.StoreShapeKnownAppHostOnlyEdges);

        Action appHostGate = sut.EveryAppHostServiceReference_HasAMatchingBicepServiceDiscoveryEntry;
        Action bicepGate = sut.EveryBicepServiceDiscoveryEntry_HasAMatchingAppHostReference;

        appHostGate.Should().NotThrow();
        bicepGate.Should().NotThrow();
    }

    [Fact]
    public void MissingBicepEntry_FailsTheAppHostGate()
    {
        var bicep = ServiceDiscoverySamples.StoreShapeBicepLines
            .Where(line => !string.Equals(line, ServiceDiscoverySamples.StoreSalesToIdentityLine, StringComparison.Ordinal))
            .ToArray();
        bicep.Should().HaveCount(ServiceDiscoverySamples.StoreShapeBicepLines.Length - 1, "the sample must carry the line this test removes");
        var sut = WriteStoreShape(bicep, ServiceDiscoverySamples.StoreShapeKnownAppHostOnlyEdges);

        Action act = sut.EveryAppHostServiceReference_HasAMatchingBicepServiceDiscoveryEntry;

        act.Should().Throw<Exception>().WithMessage("*sales->identity*");
    }

    [Fact]
    public void ExtraBicepEntry_FailsTheBicepGate()
    {
        var bicep = ServiceDiscoverySamples.StoreShapeBicepLines
            .SelectMany(line => string.Equals(line, ServiceDiscoverySamples.StoreSalesToIdentityLine, StringComparison.Ordinal)
                ? [line, "              { name: 'services__gateway__http__0', value: 'http://${gatewayApp.name}' }"]
                : new[] { line })
            .ToArray();
        var sut = WriteStoreShape(bicep, ServiceDiscoverySamples.StoreShapeKnownAppHostOnlyEdges);

        Action act = sut.EveryBicepServiceDiscoveryEntry_HasAMatchingAppHostReference;

        act.Should().Throw<Exception>().WithMessage("*sales->gateway*");
    }

    [Fact]
    public void AppHostOnlyEdgeMissingFromTheKnownList_FailsTheAppHostGate()
    {
        var known = ServiceDiscoverySamples.StoreShapeKnownAppHostOnlyEdges
            .Where(entry => !string.Equals(entry.Key, "ui->gateway", StringComparison.Ordinal))
            .ToDictionary(StringComparer.Ordinal);
        var sut = WriteStoreShape(ServiceDiscoverySamples.StoreShapeBicepLines, known);

        Action act = sut.EveryAppHostServiceReference_HasAMatchingBicepServiceDiscoveryEntry;

        act.Should().Throw<Exception>().WithMessage("*ui->gateway*");
    }

    [Fact]
    public void KnownEdgeWithoutAReason_FailsTheAppHostGate()
    {
        var known = new Dictionary<string, string>(ServiceDiscoverySamples.StoreShapeKnownAppHostOnlyEdges, StringComparer.Ordinal)
        {
            ["ui->sales"] = " ",
        };
        var sut = WriteStoreShape(ServiceDiscoverySamples.StoreShapeBicepLines, known);

        Action act = sut.EveryAppHostServiceReference_HasAMatchingBicepServiceDiscoveryEntry;

        act.Should().Throw<Exception>().WithMessage("*ui->sales*");
    }

    [Fact]
    public void DefaultRepositoryRoot_IsTheNearestDirectoryHoldingTheAppHostProgram()
    {
        var assemblyFile = Path.GetFileName(typeof(AppHostBicepParityTestsBaseTests).Assembly.Location);
        var sut = new LocatorProbe(assemblyFile);

        sut.ExposedRepositoryRoot.Should().Be(new DirectoryInfo(AppContext.BaseDirectory).FullName);
    }

    [Fact]
    public void DefaultRepositoryRoot_WithNoMatchingAncestor_Throws()
    {
        var sut = new LocatorProbe(Path.Combine("no-such-directory", Guid.NewGuid().ToString("N"), "Program.cs"));

        Func<string> act = () => sut.ExposedRepositoryRoot;

        act.Should().Throw<InvalidOperationException>().WithMessage("*no-such-directory*");
    }

    private Probe WriteStoreShape(IEnumerable<string> bicepLines, IReadOnlyDictionary<string, string> knownEdges)
    {
        File.WriteAllLines(Path.Combine(_root, "Program.cs"), ServiceDiscoverySamples.StoreShapeAppHostLines);
        Directory.CreateDirectory(Path.Combine(_root, "infra"));
        File.WriteAllLines(Path.Combine(_root, "infra", "main.bicep"), bicepLines);
        return new Probe(_root, knownEdges);
    }

    /// <summary>A subclass over a temporary root; private, so xUnit does not collect its inherited facts.</summary>
    private sealed class Probe(string root, IReadOnlyDictionary<string, string> knownEdges) : AppHostBicepParityTestsBase
    {
        protected override string AppHostProgramPath => "Program.cs";

        protected override string RepositoryRoot => root;

        protected override IReadOnlyDictionary<string, string> KnownAppHostOnlyEdges => knownEdges;
    }

    /// <summary>A subclass that keeps the default repository-root locator.</summary>
    private sealed class LocatorProbe(string appHostProgramPath) : AppHostBicepParityTestsBase
    {
        public string ExposedRepositoryRoot => RepositoryRoot;

        protected override string AppHostProgramPath => appHostProgramPath;
    }
}
