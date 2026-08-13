using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// Pins the anti-spray defaults on <see cref="AuthControllerBase"/>. These are attributes, so
/// nothing else fails if one is dropped: the endpoint simply stops being throttled and the app keeps
/// serving traffic. That is exactly how MMCA.Store ended up with no spray protection while the
/// framework "shipped" the policy, so the presence (and deliberate absence) of the attribute is
/// asserted directly.
/// </summary>
public sealed class AuthControllerBaseRateLimitTests
{
    [Theory]
    [InlineData(nameof(AuthControllerBase.LoginAsync))]
    [InlineData(nameof(AuthControllerBase.RegisterAsync))]
    public void AnonymousCredentialEndpoint_CarriesTheAuthIpPolicy(string actionName)
    {
        var attribute = Action(actionName).GetCustomAttribute<EnableRateLimitingAttribute>();

        attribute.Should().NotBeNull(
            because: $"{actionName} is anonymous and credential-bearing, so it must be throttled per client IP by default");
        attribute!.PolicyName.Should().Be(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp);
    }

    // Not an oversight: refresh is automatic and periodic, and Blazor Server issues it server-side,
    // so every Server-circuit user shares the UI host's IP. A per-IP window would throttle ordinary
    // token renewal for everyone behind that host. Asserted so the "consistency" fix of adding it
    // has to argue with this test first.
    [Fact]
    public void RefreshEndpoint_IsDeliberatelyNotThrottledPerIp() =>
        Action(nameof(AuthControllerBase.RefreshAsync))
            .GetCustomAttribute<EnableRateLimitingAttribute>()
            .Should().BeNull(
                because: "refresh is automatic and shares the UI host's IP under Blazor Server, so a per-IP window would throttle legitimate token renewal");

    // The framework's own answer, recorded instead of assumed: [EnableRateLimiting] declares
    // Inherited, which is the mechanism the override pin below relies on. ADR-019 now cites this
    // fact, so it is asserted here rather than left standing as an unverified claim about
    // framework internals.
    [Fact]
    public void EnableRateLimitingAttribute_IsDeclaredInherited() =>
        typeof(EnableRateLimitingAttribute).GetCustomAttribute<AttributeUsageAttribute>()!.Inherited
            .Should().BeTrue(
                because: "ADR-019 cites attribute inheritance as the reason a derived override keeps the per-IP policy");

    // A derived controller that overrides RegisterAsync without re-applying the attribute still
    // resolves the policy through the base method, so an override cannot silently drop the throttle.
    // Consumers nonetheless apply the attribute explicitly on every override by convention (ADC and
    // Store both do): this pins the mechanism, it does not bless a bare override.
    [Fact]
    public void DerivedOverride_WithoutTheAttribute_StillCarriesTheAuthIpPolicy()
    {
        MethodInfo? overriddenAction = typeof(OverridingAuthController).GetMethod(
            nameof(AuthControllerBase.RegisterAsync),
            BindingFlags.Public | BindingFlags.Instance);

        overriddenAction.Should().NotBeNull(
            because: "the test double must really override RegisterAsync for this pin to mean anything");
        overriddenAction!.DeclaringType.Should().Be<OverridingAuthController>();

        EnableRateLimitingAttribute[] inheritedPolicies = overriddenAction
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToArray();

        inheritedPolicies.Should().ContainSingle(
            because: "the attribute is inherited, so an override that omits it still throttles per client IP");
        inheritedPolicies[0].PolicyName.Should().Be(WebApplicationBuilderExtensions.RateLimitPolicyAuthIp);
    }

    private static MethodInfo Action(string name) =>
        typeof(AuthControllerBase).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"AuthControllerBase.{name} not found; this guard must follow the base's action names.");

    // Stands in for a consumer controller that overrides the action and leaves the attribute off.
    // Only its type is read, by reflection, so it is never instantiated and the override body is
    // deliberately inert.
    private sealed class OverridingAuthController(
        IAuthenticationService authenticationService,
        ICurrentUserService currentUserService) : AuthControllerBase(authenticationService, currentUserService)
    {
        public override Task<ActionResult<AuthenticationResponse>> RegisterAsync(
            RegisterRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<ActionResult<AuthenticationResponse>>(
                StatusCode(StatusCodes.Status501NotImplemented));
    }
}
