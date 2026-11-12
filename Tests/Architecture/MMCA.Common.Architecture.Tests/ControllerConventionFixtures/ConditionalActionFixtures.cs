using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Controllers;

namespace MMCA.Common.Architecture.Tests.ControllerConventionFixtures;

/// <summary>
/// Compiled conditional-action shapes for <c>ConditionalActionResponseRuleTests</c>: one controller
/// per way of satisfying or breaking the rule that an action carrying <c>[SupportsIfMatch]</c> never
/// declares 412 or 428 by hand (the attribute contributes both). One violation per type, so each test
/// can scan exactly the shape it names. No POST and no constructor dependency, so the other
/// fixture-assembly scans are unaffected.
/// </summary>
public sealed class Conditional412FixtureController : ApiControllerBase
{
    [HttpPut("declares-412")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public IActionResult Declares412() => NoContent();
}

/// <summary>Violation: a typed hand-written 428.</summary>
public sealed class Conditional428FixtureController : ApiControllerBase
{
    [HttpPut("declares-428")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired, Type = typeof(ProblemDetails))]
    public IActionResult Declares428() => NoContent();
}

/// <summary>Violation: the generic attribute derives from <c>ProducesResponseTypeAttribute</c>, so it counts too.</summary>
public sealed class ConditionalGeneric412FixtureController : ApiControllerBase
{
    [HttpPut("declares-generic-412")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    public IActionResult DeclaresGeneric412() => NoContent();
}

/// <summary>Compliant: a conditional action that leaves 412 and 428 to the attribute.</summary>
public sealed class ConditionalCleanFixtureController : ApiControllerBase
{
    [HttpPut("declares-neither")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public IActionResult DeclaresNeither() => NoContent();
}

/// <summary>Compliant: a 412 on an action that is not conditional is outside the rule.</summary>
public sealed class NonConditional412FixtureController : ApiControllerBase
{
    [HttpPut("not-conditional")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public IActionResult NotConditional() => NoContent();
}
