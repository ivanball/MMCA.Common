using MMCA.Common.Architecture.Tests.TenantScopeFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Domain.EntityModel;

/// <summary>
/// Self-test for the tenancy rules shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="TenantScopeConventionTestsBase"/>). The map declares THIS assembly as a module's Domain
/// layer, so the fixtures in <c>TenantScopeFixtures</c> are module entities, and the tests pin each
/// behaviour: a scoped entity passes, a forgotten marker is flagged, a declared-global entity passes,
/// and a tenant column without the marker is flagged even when declared global.
/// </summary>
public sealed class TenantScopeFitnessTests
{
    private const string FixtureNamespace = "MMCA.Common.Architecture.Tests.TenantScopeFixtures";

    private readonly FixtureAssemblyMap _map = new();

    [Fact]
    public void UnscopedModuleEntity_IsFlagged()
    {
        var act = () => ArchitectureRules.ModuleEntitiesAreTenantScopedOrDeclaredGlobal(_map, []);

        act.Should().Throw<XunitException>()
            .WithMessage($"*{nameof(UnscopedTenantFixture)}*", "an entity with no marker shares its rows across tenants");
    }

    [Fact]
    public void ScopedModuleEntity_IsNotFlagged()
    {
        var act = () => ArchitectureRules.ModuleEntitiesAreTenantScopedOrDeclaredGlobal(_map, []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().NotContain(nameof(ScopedTenantFixture), "ITenantEntity is the scope marker");
    }

    [Fact]
    public void DeclaredGlobalEntity_IsNotFlagged()
    {
        var global = $"{FixtureNamespace}.{nameof(UnscopedTenantFixture)}";

        var act = () => ArchitectureRules.ModuleEntitiesAreTenantScopedOrDeclaredGlobal(_map, [global]);

        act.Should().Throw<XunitException>("other unscoped entities in this assembly are still undeclared")
            .Which.Message.Should().NotContain(nameof(UnscopedTenantFixture));
    }

    [Fact]
    public void TenantColumnWithoutMarker_IsFlagged()
    {
        var act = () => ArchitectureRules.EntitiesWithTenantIdImplementTenantMarker(_map);

        var message = act.Should().Throw<XunitException>().Which.Message;

        message.Should().Contain(nameof(TenantColumnWithoutMarkerFixture));
        message.Should().NotContain(nameof(ScopedTenantFixture));
        message.Should().NotContain(nameof(UnscopedTenantFixture), "an entity with no tenant column is the module rule's concern");
    }

    [Fact]
    public void FrameworkOnlyMap_HasNoModuleEntities_AndTheRuleSaysSo()
    {
        var act = () => ArchitectureRules.ModuleEntitiesAreTenantScopedOrDeclaredGlobal(new CommonArchitectureMap(), []);

        act.Should().Throw<XunitException>()
            .WithMessage("*module entities*", "a rule that finds nothing to check must fail rather than pass vacuously");
    }

    /// <summary>A map that declares this test assembly as a module's Domain layer.</summary>
    private sealed class FixtureAssemblyMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Fixtures", Layer.Domain, typeof(ScopedTenantFixture).Assembly),
        ];
    }
}
