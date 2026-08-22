using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

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

    private sealed class ConformantTests : AnonymousEndpointTestsBase
    {
        protected override IReadOnlyCollection<Assembly> TargetAssemblies =>
            [typeof(AnonymousEndpointTestsBaseTests).Assembly];

        protected override IReadOnlyCollection<string> AllowedAnonymousEndpoints =>
        [
            $"{typeof(AnonymousFixtureController).FullName}.{nameof(AnonymousFixtureController.PeekAsync)}",
            $"{typeof(AbstractAnonymousFixtureControllerBase).FullName}.{nameof(AbstractAnonymousFixtureControllerBase.InheritedAnonymousAsync)}",
            typeof(TypeLevelAnonymousFixtureController).FullName!,
        ];

        internal IReadOnlyCollection<string> AnonymousEndpointsForTest() => [.. AnonymousEndpoints()];
    }
}
