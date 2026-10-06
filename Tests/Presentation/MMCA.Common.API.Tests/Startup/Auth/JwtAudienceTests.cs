using AwesomeAssertions;
using MMCA.Common.API.Startup.Auth;

namespace MMCA.Common.API.Tests.Startup.Auth;

/// <summary>
/// Unit tests for the fail-closed JWT audience guard every service host runs before
/// <c>AddForwardedJwtBearer</c>. The key and the failure message are part of the contract: the key is
/// what appsettings and the deployment templates set, and the message is the only thing that tells an
/// operator which setting is missing.
/// </summary>
public sealed class JwtAudienceTests
{
    [Fact]
    public void ConfiguredAudience_IsReturned() =>
        JwtAudience.RequireConfigured("MyAppApi").Should().Be("MyAppApi");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOrBlankAudience_ThrowsNamingTheKeyAndTheEnvironmentVariable(string? configured)
    {
        var act = () => JwtAudience.RequireConfigured(configured);

        act.Should().Throw<InvalidOperationException>().WithMessage(
            "Jwt:Audience is not configured. Set it in the service's appsettings.json or as the Jwt__Audience environment variable.");
    }

    [Fact]
    public void ConfigKey_IsTheOneTheServiceHostsAndTemplatesSet() =>
        JwtAudience.ConfigKey.Should().Be(
            "Jwt:Audience",
            "appsettings.json and the Bicep Jwt__Audience setting use this key; renaming it silently breaks every service host");
}
