using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using MMCA.Common.API.Authorization;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Authorization;

/// <summary>
/// "An endpoint where a person takes part refuses a machine token." A machine client (OAuth client
/// credentials, ADR-140) signs in as a service account whose token carries <c>token_use</c> =
/// <c>machine</c>; <see cref="RefuseMachineClientsAttribute"/> short-circuits such a caller with 403 and
/// leaves every other caller alone, so being signed in is still decided by <c>[Authorize]</c>.
/// <para>
/// The attribute is exercised as an <see cref="IAuthorizationFilter"/>, the observable result being the
/// <see cref="AuthorizationFilterContext.Result"/> it leaves behind. The refusal is shaped like the
/// framework's other filter refusals (<c>SupportsIfMatchAttribute</c>, <c>IdempotencyFilter</c>): an
/// <see cref="ObjectResult"/> carrying a <see cref="ProblemDetails"/>.
/// </para>
/// </summary>
public sealed class RefuseMachineClientsAttributeTests
{
    [Fact]
    public void MachineToken_IsRefusedWith403()
    {
        var context = CreateContext(SignedIn(new Claim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine)));

        Run(context);

        context.Result.Should().NotBeNull("a machine token must not reach a participation action");
        context.Result.Should().BeAssignableTo<IStatusCodeActionResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void MachineToken_RefusalBody_IsAProblemDetailsWithStatus403()
    {
        var context = CreateContext(SignedIn(new Claim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine)));

        Run(context);

        var objectResult = context.Result.Should().BeAssignableTo<ObjectResult>(
            "the framework's filters answer a refusal with a problem-details body, never a bare status code").Which;
        objectResult.Value.Should().BeAssignableTo<ProblemDetails>()
            .Which.Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void SignedInPerson_WithoutTheClaim_PassesThrough()
    {
        var context = CreateContext(SignedIn(new Claim(AuthClaimTypes.Subject, "42")));

        Run(context);

        context.Result.Should().BeNull("a token without token_use was issued to a person");
    }

    [Fact]
    public void TokenUseWithAnotherValue_PassesThrough()
    {
        var context = CreateContext(SignedIn(
            new Claim(AuthClaimTypes.Subject, "42"),
            new Claim(AuthClaimTypes.TokenUse, "person")));

        Run(context);

        context.Result.Should().BeNull("only the value 'machine' marks a machine token");
    }

    [Fact]
    public void AnonymousCaller_PassesThrough()
    {
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));

        Run(context);

        context.Result.Should().BeNull("whether the caller must be signed in is [Authorize]'s decision, not this filter's");
    }

    [Fact]
    public void Attribute_CanBeDeclaredOnAClassAndOnAMethod()
    {
        AttributeUsageAttribute usage = typeof(RefuseMachineClientsAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        usage.ValidOn.Should().Be(
            AttributeTargets.Class | AttributeTargets.Method,
            "a participation controller refuses machine tokens for every action at class level, and a single action can opt in alone");
    }

    // -- Helpers --
    private static void Run(AuthorizationFilterContext context) =>
        new RefuseMachineClientsAttribute().OnAuthorization(context);

    private static ClaimsPrincipal SignedIn(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));

    private static AuthorizationFilterContext CreateContext(ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, []);
    }
}
