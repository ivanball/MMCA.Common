using Microsoft.AspNetCore.Mvc;
using MMCA.Common.API.Controllers;

namespace MMCA.Common.Architecture.Tests.RuleShapeFixtures;

/// <summary>
/// Compiled controller shapes for <c>ControllerShapeRuleTests</c>. None declares an action, so the
/// endpoint, idempotency and authorization scans that read this same assembly see nothing new.
/// </summary>
public sealed class ShapeOrdersController : ApiControllerBase;

/// <summary>Violation of the sealing rule: a controller by base type that is not sealed.</summary>
public class ShapeOpenOrdersController : ApiControllerBase;

/// <summary>Violation of the sealing rule: a controller by base type alone (no "Controller" suffix).</summary>
[ApiController]
public class ShapeOpenReportsEndpoint : ControllerBase;

/// <summary>Violation of the sealing rule: a controller by name alone (no controller base).</summary>
public class ShapeOpenPlainController
{
    public string Route { get; } = "plain";
}

/// <summary>Not a controller by name or base, so the sealing rule does not read it.</summary>
public class ShapeOpenRouteHelper
{
    public string Route { get; } = "helper";
}

/// <summary>Violation of the base rule: derives from the raw <see cref="ControllerBase"/>.</summary>
[ApiController]
public sealed class ShapeRawController : ControllerBase;

/// <summary>Derives from the raw base too; the tests exempt it by full name, as a payment webhook would be.</summary>
[ApiController]
public sealed class ShapeWebhookController : ControllerBase;
