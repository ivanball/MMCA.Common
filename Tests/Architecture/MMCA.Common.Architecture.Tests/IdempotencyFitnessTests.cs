using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Verifies the <c>PostActionsDeclareIdempotencyIntent</c> fitness function against fake controllers:
/// it flags a POST that declares nothing, and accepts one covered directly, one covered through an
/// inherited base action, and one that opted out with a justification.
/// </summary>
public sealed class IdempotencyFitnessTests
{
    [Fact]
    public void Rule_FlagsThePostThatDeclaresNothing()
    {
        var act = () => ArchitectureRules.PostActionsDeclareIdempotencyIntent(new IdempotencyTestMap());

        var exception = act.Should().Throw<Exception>().Which;
        exception.Message.Should().Contain(
            $"{nameof(UndeclaredFitnessController)}.{nameof(UndeclaredFitnessController.CreateThingAsync)}",
            "a POST with neither attribute is exactly what the gate exists to catch");
    }

    [Fact]
    public void Rule_AcceptsDirectAndInheritedAndOptedOutDeclarations()
    {
        var act = () => ArchitectureRules.PostActionsDeclareIdempotencyIntent(new IdempotencyTestMap());

        var exception = act.Should().Throw<Exception>().Which;
        exception.Message.Should().NotContain(
            nameof(IdempotentFitnessController),
            "[Idempotent] on the action itself is the primary way to declare intent");
        exception.Message.Should().NotContain(
            nameof(InheritingFitnessController),
            "a derived controller reflects the base's action, so the base's attribute covers it");
        exception.Message.Should().NotContain(
            nameof(NonIdempotentFitnessController),
            "a justified opt-out is a declaration, not an omission");
    }

    [Fact]
    public void Rule_SkipsAbstractControllers()
    {
        var act = () => ArchitectureRules.PostActionsDeclareIdempotencyIntent(new IdempotencyTestMap());

        act.Should().Throw<Exception>().Which.Message.Should().NotContain(
            nameof(AbstractFitnessControllerBase),
            "an abstract base is a declaration site; its concrete subclasses are what route");
    }

    private sealed class IdempotencyTestMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
            [Framework(Layer.Api, typeof(IdempotencyFitnessTests).Assembly)];
    }

    /// <summary>Declares intent on the action itself.</summary>
    public sealed class IdempotentFitnessController : ControllerBase
    {
        /// <summary>A covered create.</summary>
        /// <returns>An empty 200.</returns>
        [HttpPost]
        [Idempotent]
        public IActionResult CreateThingAsync() => Ok();
    }

    /// <summary>Declares intent once, for every controller that inherits the action.</summary>
    public abstract class AbstractFitnessControllerBase : ControllerBase
    {
        /// <summary>A covered create, inherited rather than redeclared.</summary>
        /// <returns>An empty 200.</returns>
        [HttpPost("inherited")]
        [Idempotent]
        public virtual IActionResult CreateInheritedAsync() => Ok();
    }

    /// <summary>Inherits the base action and, with it, the base's declaration.</summary>
    public sealed class InheritingFitnessController : AbstractFitnessControllerBase;

    /// <summary>Opts out, with a reason.</summary>
    public sealed class NonIdempotentFitnessController : ControllerBase
    {
        /// <summary>A deliberately non-replayable POST.</summary>
        /// <returns>An empty 200.</returns>
        [HttpPost]
        [NonIdempotent("Issues a token; a replayed response would hand back credentials minted for an earlier call.")]
        public IActionResult IssueTokenAsync() => Ok();
    }

    /// <summary>Declares nothing: the offender.</summary>
    public sealed class UndeclaredFitnessController : ControllerBase
    {
        /// <summary>An undeclared create.</summary>
        /// <returns>An empty 200.</returns>
        [HttpPost]
        public IActionResult CreateThingAsync() => Ok();
    }
}
