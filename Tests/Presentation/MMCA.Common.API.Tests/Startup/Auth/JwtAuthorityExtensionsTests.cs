using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.API.Startup.Auth;

namespace MMCA.Common.API.Tests.Startup.Auth;

/// <summary>
/// Unit tests for the JWKS-authority guard every extracted service ran inline before
/// <c>AddForwardedJwtBearer</c>. The failure message is part of the contract: it is the only thing
/// that tells an operator the AppHost never wired <c>WithJwksDiscovery</c>, so it is asserted
/// verbatim rather than by type alone.
/// </summary>
public sealed class JwtAuthorityExtensionsTests
{
    private const string ExpectedMessage =
        "Authentication:JwtBearer:Authority is not configured. " +
        "Wire .WithJwksDiscovery(identityService) in the AppHost.";

    private static IConfiguration Config(string? authority) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [JwtAuthorityExtensions.JwtAuthorityConfigKey] = authority,
            })
            .Build();

    [Fact]
    public void ConfiguredAuthority_IsReturned() =>
        Config("http://identity").GetRequiredJwtAuthority().Should().Be("http://identity");

    [Fact]
    public void MissingAuthority_ThrowsWithTheWiringInstruction()
    {
        var act = () => Config(null).GetRequiredJwtAuthority();

        act.Should().Throw<InvalidOperationException>().WithMessage(ExpectedMessage);
    }

    [Fact]
    public void BlankAuthority_IsPassedThroughUnchanged() =>
        // Exactly what the five inline guards this replaced did: a null check, not a blank check.
        // A configured-but-blank authority is rejected one line later by AddForwardedJwtBearer's own
        // ArgumentException.ThrowIfNullOrWhiteSpace, so the guard deliberately does not duplicate it
        // and the hoist stays behavior-identical.
        Config(string.Empty).GetRequiredJwtAuthority().Should().BeEmpty();

    [Fact]
    public void ConfigKey_MatchesTheOneTheAppHostInjects() =>
        JwtAuthorityExtensions.JwtAuthorityConfigKey.Should().Be(
            "Authentication:JwtBearer:Authority",
            "WithJwksDiscovery sets Authentication__JwtBearer__Authority; renaming the key silently breaks every service host");

    [Fact]
    public void NullConfiguration_IsRejected()
    {
        IConfiguration configuration = null!;

        var act = () => configuration.GetRequiredJwtAuthority();

        act.Should().Throw<ArgumentNullException>();
    }
}
