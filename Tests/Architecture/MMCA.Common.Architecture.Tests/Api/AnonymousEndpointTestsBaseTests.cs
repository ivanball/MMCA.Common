using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Caching;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Architecture.Tests.ControllerConventionFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Adversarial coverage for <see cref="AnonymousEndpointTestsBase"/>: each assertion must actually
/// FAIL on the drift it claims to catch, and the identifier shapes the allow-list is written in must
/// match what the scan emits. The drifted subclasses are private so xUnit does not collect their
/// inherited facts as (deliberately failing) tests of their own.
/// </summary>
public sealed class AnonymousEndpointTestsBaseTests
{
    [Fact]
    public void Base_Fails_WhenAnAnonymousEndpointIsNotAllowListed()
    {
        var assert = new DriftedTests().AnonymousEndpoints_AreAllowListed;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                nameof(AnonymousFixtureController),
                "the offender message must name the endpoint that lost its gate");
    }

    [Fact]
    public void Base_Fails_WhenTheAllowListHasAStaleEntry()
    {
        var assert = new StaleAllowListTests().AllowList_HasNoStaleEntries;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                "NoLongerAnonymous",
                "an entry matching nothing must be reported rather than silently ignored");
    }

    [Fact]
    public void Base_Fails_WhenNothingWasScanned()
    {
        var assert = new EmptyScanTests().ScannedEndpointSet_IsNotEmpty;

        assert.Should().Throw<Exception>();
    }

    [Fact]
    public void Base_Accepts_TypeLevelAndMethodLevelEntries()
    {
        var conformant = new ConformantTests();

        var assert = () =>
        {
            conformant.AnonymousEndpoints_AreAllowListed();
            conformant.AllowList_HasNoStaleEntries();
            conformant.ScannedEndpointSet_IsNotEmpty();
        };

        assert.Should().NotThrow(
            "both identifier shapes the allow-list is written in must match what the scan emits");
    }

    [Fact]
    public void Base_DoesNotReport_AnInheritedAttributeOnTheDerivedController()
    {
        // The attribute is declared once on the abstract base, exactly like the framework's
        // AuthControllerBase actions: the derived controller must not surface a second occurrence,
        // or every consumer repo would have to allow-list the framework's endpoints again.
        var reported = new ConformantTests().AnonymousEndpointsForTest();

        reported.Should().NotContain(
            $"{typeof(InheritingFixtureController).FullName}.{nameof(AbstractAnonymousFixtureControllerBase.InheritedAnonymousAsync)}");
    }

    // ── Undecorated endpoints (SEC-ADC-03) ──
    [Fact]
    public void Base_Fails_WhenAControllerCarriesNoAuthorizationDecisionAtAll()
    {
        var assert = new StrictDriftedTests().Endpoints_DeclareAnAuthorizationDecision;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(
                nameof(UndecoratedFixtureController),
                "a controller that forgot [Authorize] is invisible to the [AllowAnonymous] allow-list, "
                + "which is the whole gap this check exists to close");
    }

    [Fact]
    public void Base_DoesNotReport_AControllerThatInheritsItsDecisionFromAnAbstractBase()
    {
        var reported = new StrictConformantTests().UndecoratedEndpointsForTest();

        reported.Should().NotContain(
            typeof(InheritingFixtureController).FullName!,
            "the decision was made once on the base's action, exactly as ASP.NET Core resolves it");
        reported.Should().NotContain(
            typeof(AbstractAnonymousFixtureControllerBase).FullName!,
            "an abstract base is never routed, so it declares no endpoint of its own");
    }

    [Fact]
    public void Base_Accepts_AnUndecoratedEndpointThatIsAllowListed()
    {
        var assert = new StrictConformantTests().Endpoints_DeclareAnAuthorizationDecision;

        assert.Should().NotThrow("a reviewed entry is what the allow-list is for");
    }

    [Fact]
    public void Base_Fails_WhenTheUndecoratedAllowListHasAStaleEntry()
    {
        var assert = new StaleUndecoratedAllowListTests().UndecoratedAllowList_HasNoStaleEntries;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain("NoLongerUndecorated");
    }

    // L143 (decision D7): the gate is on by default. The old off-by-default branch asserted that two
    // disjoint-by-construction scans did not intersect, which could never fail.
    [Fact]
    public void Base_RequiresAnExplicitDecisionByDefault()
    {
        var assert = new DriftedTests().Endpoints_DeclareAnAuthorizationDecision;

        assert.Should().Throw<Exception>()
            .Which.Message.Should().Contain(nameof(UndecoratedFixtureController));
    }

    // -- [PublicCachedReads] (item 6, rules 10 and 11) --
    [Fact]
    public void PublicCachedReadsType_YieldsThreeReadIdentifiers()
    {
        var reported = new ConformantTests().AnonymousEndpointsForTest();
        var direct = typeof(PublicCachedReadsFixtureController).FullName!;
        var declaringBase = typeof(PublicCachedReadsFixtureControllerBase).FullName!;
        var inheriting = typeof(InheritsPublicCachedReadsFixtureController).FullName!;

        reported.Where(e => e.StartsWith(direct + ".", StringComparison.Ordinal)).Should().BeEquivalentTo(
            [$"{direct}.GetAllAsync", $"{direct}.GetAllForLookupAsync", $"{direct}.GetByIdAsync"],
            "the attribute opens exactly the base reads, under the identifiers a deleted override produced");
        reported.Should().NotContain(direct, "the attribute is not a type-level [AllowAnonymous]");
        reported.Where(e => e.StartsWith(declaringBase + ".", StringComparison.Ordinal)).Should().BeEquivalentTo(
            [$"{declaringBase}.GetAllAsync", $"{declaringBase}.GetAllForLookupAsync", $"{declaringBase}.GetByIdAsync"],
            "an abstract base carrying the attribute is a scanned declaration site too");
        reported.Should().NotContain(
            e => e.StartsWith(inheriting, StringComparison.Ordinal),
            "only a type's OWN attributes are read, so an inherited attribute is reported once, at its declaration");
    }

    [Fact]
    public void PublicCachedReadsType_CountsAsAuthorizationDecision()
    {
        var reported = new StrictConformantTests().UndecoratedEndpointsForTest();

        reported.Should().NotContain(
            typeof(PublicCachedReadsFixtureController).FullName!,
            "[PublicCachedReads] decides access for the reads it opens");
        reported.Should().NotContain(
            typeof(InheritsPublicCachedReadsFixtureController).FullName!,
            "the attribute counts anywhere in the type's base chain");
    }

    /// <summary>Carries <c>[PublicCachedReads]</c> itself and no other authorization attribute.</summary>
    [PublicCachedReads("FixtureCache")]
    public sealed class PublicCachedReadsFixtureController(
        IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
        ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
        : EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>(queryService, logger);

    /// <summary>Declares <c>[PublicCachedReads]</c> once, for every controller that inherits it.</summary>
    [PublicCachedReads("FixtureCache")]
    public abstract class PublicCachedReadsFixtureControllerBase(
        IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
        ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
        : EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>(queryService, logger);

    /// <summary>Inherits the base's attribute without redeclaring it, and carries nothing else.</summary>
    public sealed class InheritsPublicCachedReadsFixtureController(
        IEntityQueryService<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int> queryService,
        ILogger<EntityControllerBase<ControllerConventionFixtureEntity, ControllerConventionFixtureDto, int>> logger)
        : PublicCachedReadsFixtureControllerBase(queryService, logger);

    /// <summary>Carries no authorization attribute at all: the forgotten-[Authorize] shape.</summary>
    public sealed class UndecoratedFixtureController : ControllerBase
    {
        /// <summary>An action whose only gate would be the fallback policy.</summary>
        /// <returns>An empty 200.</returns>
        [HttpGet("undecorated")]
        public IActionResult ListAsync() => Ok();
    }

    /// <summary>Carries the anonymous action the drifted subclass deliberately fails to allow-list.</summary>
    public sealed class AnonymousFixtureController : ControllerBase
    {
        /// <summary>An ungated action.</summary>
        /// <returns>An empty 200.</returns>
        [HttpGet]
        [AllowAnonymous]
        public IActionResult PeekAsync() => Ok();
    }

    /// <summary>Declares an anonymous action once, for every controller that inherits it.</summary>
    public abstract class AbstractAnonymousFixtureControllerBase : ControllerBase
    {
        /// <summary>An ungated action declared on the base only.</summary>
        /// <returns>An empty 200.</returns>
        [HttpGet("inherited")]
        [AllowAnonymous]
        public virtual IActionResult InheritedAnonymousAsync() => Ok();
    }

    /// <summary>Inherits the base's anonymous action without redeclaring it.</summary>
    public sealed class InheritingFixtureController : AbstractAnonymousFixtureControllerBase;

    /// <summary>Anonymous at the type level, the other identifier shape the allow-list accepts.</summary>
    [AllowAnonymous]
    public sealed class TypeLevelAnonymousFixtureController : ControllerBase;

    private sealed class DriftedTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints => [];
    }

    private sealed class StaleAllowListTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints =>
            ["MMCA.Common.Architecture.Tests.NoLongerAnonymousController.ReadAsync"];
    }

    private sealed class EmptyScanTests : AnonymousEndpointTestsBase
    {
        // The Shared package has neither controllers nor routable components, so the scan is empty.
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(Shared.Abstractions.Result).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints => [];
    }

    private sealed class StrictDriftedTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints => [];

        protected override bool RequireExplicitAuthorizationDecision => true;
    }

    private sealed class StaleUndecoratedAllowListTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints => [];

        protected override IReadOnlyCollection<string> EndpointsWithoutAuthorizationAttribute =>
            ["MMCA.Common.Architecture.Tests.NoLongerUndecoratedController"];
    }

    private sealed class StrictConformantTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints => [];

        protected override bool RequireExplicitAuthorizationDecision => true;

        protected override IReadOnlyCollection<string> EndpointsWithoutAuthorizationAttribute =>
            [.. UndecoratedEndpoints()];

        internal IReadOnlyCollection<string> UndecoratedEndpointsForTest() => [.. UndecoratedEndpoints()];
    }

    private sealed class ConformantTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints =>
        [
            $"{typeof(AnonymousFixtureController).FullName}.{nameof(AnonymousFixtureController.PeekAsync)}",
            $"{typeof(AbstractAnonymousFixtureControllerBase).FullName}.{nameof(AbstractAnonymousFixtureControllerBase.InheritedAnonymousAsync)}",
            typeof(TypeLevelAnonymousFixtureController).FullName!,

            // [PublicCachedReads] fixtures in this assembly: the three read identifiers each (rule 10).
            $"{typeof(PublicCachedReadsFixtureController).FullName}.GetAllAsync",
            $"{typeof(PublicCachedReadsFixtureController).FullName}.GetAllForLookupAsync",
            $"{typeof(PublicCachedReadsFixtureController).FullName}.GetByIdAsync",
            $"{typeof(PublicCachedReadsFixtureControllerBase).FullName}.GetAllAsync",
            $"{typeof(PublicCachedReadsFixtureControllerBase).FullName}.GetAllForLookupAsync",
            $"{typeof(PublicCachedReadsFixtureControllerBase).FullName}.GetByIdAsync",

            // ControllerConventionFixtures carrying [AllowAnonymous] (ControllerReadOverrideRuleTests).
            $"{typeof(AllowAnonymousReadOverrideFixtureController).FullName}.{nameof(AllowAnonymousReadOverrideFixtureController.GetByIdAsync)}",
            $"{typeof(AppActionMarkersFixtureController).FullName}.{nameof(AppActionMarkersFixtureController.Search)}",
        ];

        internal IReadOnlyCollection<string> AnonymousEndpointsForTest() => [.. AnonymousEndpoints()];
    }
}
