using MMCA.Common.Architecture.Tests.ConstructorDependencyFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs;

/// <summary>
/// Adversarial coverage for the two populations <see cref="ConstructorDependencyCountTestsBase"/> gained
/// beyond Application <c>*Service</c> classes: API controllers off <c>Map.Api()</c> and CQRS handlers off
/// <c>Map.ModuleApplication()</c>. Each assertion must actually FAIL on the drift it claims to catch and
/// name the offender in the shape a consumer reads (<c>FullName (N ctor dependencies)</c>). The
/// subclasses are private so xUnit does not collect their inherited facts as (deliberately failing)
/// tests of their own.
/// </summary>
public sealed class ConstructorDependencyCountTestsBaseTests
{
    [Fact]
    public void Controllers_AreFlagged_WhenAControllerExceedsTheCeiling()
    {
        var assert = new TightCeilingTests().Controllers_DoNotExceedConstructorDependencyCeiling;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                $"{typeof(FatFixtureController).FullName} (3 ctor dependencies)",
                "the offender message must name the controller and its dependency count");
    }

    [Fact]
    public void Controllers_AtTheCeiling_AreNotFlagged()
    {
        var assert = new ConformantTests().Controllers_DoNotExceedConstructorDependencyCeiling;

        assert.Should().NotThrow(
            "the ceiling is a high-water mark: the widest controller sits exactly on it");
    }

    [Fact]
    public void Handlers_AreFlagged_WhenEitherHandlerContractExceedsTheCeiling()
    {
        var assert = new TightCeilingTests().Handlers_DoNotExceedConstructorDependencyCeiling;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain(
            $"{typeof(RebuildFixtureProjectionHandler).FullName} (3 ctor dependencies)",
            "ICommandHandler implementations are in the scanned population");
        message.Should().Contain(
            $"{typeof(GetFixtureProjectionHandler).FullName} (3 ctor dependencies)",
            "IQueryHandler implementations are in the same population");
    }

    [Fact]
    public void Handlers_AtTheCeiling_AreNotFlagged()
    {
        var assert = new ConformantTests().Handlers_DoNotExceedConstructorDependencyCeiling;

        assert.Should().NotThrow(
            "the ceiling is a high-water mark: the widest handler sits exactly on it");
    }

    [Fact]
    public void BothPopulations_FailVacuousScans()
    {
        var empty = new EmptyScanTests();

        var controllers = empty.Controllers_DoNotExceedConstructorDependencyCeiling;
        var handlers = empty.Handlers_DoNotExceedConstructorDependencyCeiling;

        controllers.Should().Throw<Exception>(
            "a map whose API assemblies hold no controller would pass while checking nothing");
        handlers.Should().Throw<Exception>(
            "a map whose Application assemblies hold no handler would pass while checking nothing");
    }

    [Fact]
    public void LooseControllerCeiling_IsFlagged_NamingPopulationCeilingAndObservedMax()
    {
        var assert = new LooseCeilingTests().Controllers_ConstructorDependencyCeilingIsTight;

        var message = assert.Should().Throw<Exception>().Which.Message;

        message.Should().Contain("API controllers", "the failure must name the population");
        message.Should().Contain("MaxControllerConstructorDependencies is 4",
            "the failure must name the ceiling property and its declared value");
        message.Should().Contain("takes 3", "the failure must name the observed maximum");
        message.Should().Contain(typeof(FatFixtureController).FullName!,
            "the failure must name the class sitting at the observed maximum");
        message.Should().Contain("from 4 to 3", "the failure must spell out the fix");
    }

    [Fact]
    public void LooseHandlerCeiling_IsFlagged()
    {
        var assert = new LooseCeilingTests().Handlers_ConstructorDependencyCeilingIsTight;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain("CQRS handlers", "a loose handler ceiling is a budget, not a ratchet");
    }

    [Fact]
    public void CeilingsAtTheObservedMaximum_AreTight()
    {
        var conformant = new ConformantTests();

        var controllers = conformant.Controllers_ConstructorDependencyCeilingIsTight;
        var handlers = conformant.Handlers_ConstructorDependencyCeilingIsTight;

        controllers.Should().NotThrow("the controller ceiling sits exactly on the widest controller");
        handlers.Should().NotThrow("the handler ceiling sits exactly on the widest handler");
    }

    [Fact]
    public void TightnessCheck_FailsVacuousScans()
    {
        var assert = new EmptyScanTests().Controllers_ConstructorDependencyCeilingIsTight;

        assert.Should().Throw<Exception>(
            "a population with nothing to measure cannot prove its ceiling is tight");
    }

    /// <summary>A map pointing both scanned layers at THIS assembly, where the fixtures compile.</summary>
    private sealed class FixtureMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Fixtures", Layer.Api, typeof(FatFixtureController).Assembly),
            Module("Fixtures", Layer.Application, typeof(FatFixtureController).Assembly),
        ];
    }

    /// <summary>A map whose layers hold neither a controller nor a handler.</summary>
    private sealed class BareMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Bare", Layer.Api, typeof(Common.Shared.Abstractions.Result).Assembly),
            Module("Bare", Layer.Application, typeof(Common.Shared.Abstractions.Result).Assembly),
        ];
    }

    private sealed class TightCeilingTests : ConstructorDependencyCountTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap();

        // The Application *Service fact is not exercised here, so its ceiling is left wide open.
        protected override int MaxConstructorDependencies => int.MaxValue;

        protected override int MaxControllerConstructorDependencies => 2;

        protected override int MaxHandlerConstructorDependencies => 2;
    }

    private sealed class ConformantTests : ConstructorDependencyCountTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap();

        protected override int MaxConstructorDependencies => int.MaxValue;

        protected override int MaxControllerConstructorDependencies => 3;

        protected override int MaxHandlerConstructorDependencies => 3;
    }

    private sealed class LooseCeilingTests : ConstructorDependencyCountTestsBase
    {
        protected override IArchitectureMap Map { get; } = new FixtureMap();

        protected override int MaxConstructorDependencies => int.MaxValue;

        protected override int MaxControllerConstructorDependencies => 4;

        protected override int MaxHandlerConstructorDependencies => 4;
    }

    private sealed class EmptyScanTests : ConstructorDependencyCountTestsBase
    {
        protected override IArchitectureMap Map { get; } = new BareMap();

        protected override int MaxConstructorDependencies => int.MaxValue;

        protected override int MaxControllerConstructorDependencies => 3;

        protected override int MaxHandlerConstructorDependencies => 3;
    }
}
