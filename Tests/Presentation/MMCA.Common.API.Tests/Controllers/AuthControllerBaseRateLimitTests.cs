using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.RateLimiting;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Startup;

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

    private static MethodInfo Action(string name) =>
        typeof(AuthControllerBase).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"AuthControllerBase.{name} not found; this guard must follow the base's action names.");
}
