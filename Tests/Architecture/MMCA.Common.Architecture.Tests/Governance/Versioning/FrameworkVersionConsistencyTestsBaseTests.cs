using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.Versioning;

/// <summary>
/// Adversarial coverage for <see cref="FrameworkVersionConsistencyTestsBase"/> (ADR-016 lockstep), the
/// gate Store and ADC subclass over their <c>Directory.Packages.props</c>. The base locates the props
/// file beside the repo's <c>{RepoToken}.slnx</c>, walking up from the test output folder, so each case
/// writes a fixture solution marker and props file into that folder and deletes both afterwards (the
/// cases of one class run one at a time, so they never share the file). The guard subclass is private
/// so xUnit does not collect its inherited fact as a test of its own.
/// </summary>
public sealed class FrameworkVersionConsistencyTestsBaseTests : IDisposable
{
    private const string FixtureToken = "FrameworkVersionFixture";

    private static readonly string PropsPath = Path.Combine(AppContext.BaseDirectory, "Directory.Packages.props");

    private static readonly string SolutionPath = Path.Combine(AppContext.BaseDirectory, $"{FixtureToken}.slnx");

    private static readonly XNamespace Msbuild = "http://schemas.microsoft.com/developer/msbuild/2003";

    public void Dispose()
    {
        File.Delete(PropsPath);
        File.Delete(SolutionPath);
    }

    [Fact]
    public void PinsInLockstep_Pass_IgnoringEverythingThatIsNotAnMmcaCommonPin()
    {
        WriteFixture(
            Pin("MMCA.Common.Shared", "1.2.3"),
            Pin("MMCA.Common.Domain", "1.2.3"),
            Pin("Other.Package", "9.0.0"),
            Pin("MMCA.CommonExtras", "4.0.0"),
            new XElement(Msbuild + "PackageReference", new XAttribute("Include", "MMCA.Common.API"), new XAttribute("Version", "7.0.0")));

        var act = new Guard(FixtureToken, minimum: 2).AllMmcaCommonPackages_ArePinnedToOneVersion;

        act.Should().NotThrow(
            "two MMCA.Common.* pins at one version meet a minimum of two; other packages, a look-alike prefix and a PackageReference are not pins of the framework");
    }

    [Fact]
    public void DivergedPins_AreFlagged_ListingEveryVersion()
    {
        WriteFixture(Pin("MMCA.Common.Shared", "1.2.3"), Pin("MMCA.Common.Domain", "1.2.4"));

        var act = new Guard(FixtureToken, minimum: 2).AllMmcaCommonPackages_ArePinnedToOneVersion;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("must share one version (ADR-016 lockstep, no phased rollout); found: 1.2.3, 1.2.4");
    }

    [Fact]
    public void PinWithoutAVersion_IsFlagged()
    {
        WriteFixture(Pin("MMCA.Common.Shared", "1.2.3"), Pin("MMCA.Common.Domain", version: null));

        var act = new Guard(FixtureToken, minimum: 2).AllMmcaCommonPackages_ArePinnedToOneVersion;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("every MMCA.Common.* PackageVersion must declare an explicit version");
    }

    [Fact]
    public void PinWithAnEmptyVersion_IsFlagged()
    {
        WriteFixture(Pin("MMCA.Common.Shared", "1.2.3"), Pin("MMCA.Common.Domain", string.Empty));

        var act = new Guard(FixtureToken, minimum: 2).AllMmcaCommonPackages_ArePinnedToOneVersion;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("must declare an explicit version");
    }

    [Fact]
    public void FewerPinsThanTheMinimum_FailAsVacuous()
    {
        WriteFixture(Pin("MMCA.Common.Shared", "1.2.3"), Pin("MMCA.Common.Domain", "1.2.3"));

        var act = new Guard(FixtureToken, minimum: 3).AllMmcaCommonPackages_ArePinnedToOneVersion;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("all 3+ MMCA.Common.* packages must be pinned in Directory.Packages.props");
    }

    [Fact]
    public void ThisRepo_DeclaresNoFrameworkPins_SoTheDefaultMinimumFails()
    {
        var act = new Guard("MMCA.Common", minimum: null).AllMmcaCommonPackages_ArePinnedToOneVersion;

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(
                "all 13+ MMCA.Common.* packages must be pinned",
                "MMCA.Common builds the packages rather than pinning them, which is why it does not subclass this base");
    }

    private static XElement Pin(string include, string? version) =>
        new(
            Msbuild + "PackageVersion",
            new XAttribute("Include", include),
            version is null ? null : new XAttribute("Version", version));

    private static void WriteFixture(params XElement[] items)
    {
        File.WriteAllText(SolutionPath, "<Solution />");
        new XDocument(new XElement(Msbuild + "Project", new XElement(Msbuild + "ItemGroup", items))).Save(PropsPath);
    }

    /// <summary>The lockstep gate over the repo a token names, with an optional minimum.</summary>
    private sealed class Guard(string repoToken, int? minimum) : FrameworkVersionConsistencyTestsBase
    {
        protected override IArchitectureMap Map { get; } = new TokenMap(repoToken);

        protected override int MinimumCommonPackageCount => minimum ?? base.MinimumCommonPackageCount;
    }

    /// <summary>A layer-less map: the gate reads only its repo token.</summary>
    private sealed class TokenMap(string repoToken) : ArchitectureMapBase
    {
        public override string RepoToken => repoToken;

        protected override IEnumerable<LayerRef> DefineLayers() => [];
    }
}
