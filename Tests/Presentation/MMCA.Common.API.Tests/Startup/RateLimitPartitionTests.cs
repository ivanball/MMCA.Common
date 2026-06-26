using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Unit tests for the global rate-limiter's exemption + partition-key logic (ADR-019).
/// These exercise the load-bearing decisions — what is bypassed, anonymous-vs-authenticated,
/// and the per-user partition-key fallback chain — directly, rather than only through a full
/// request flood. The partition/exemption helpers are exposed to this assembly via
/// <c>InternalsVisibleTo</c>.
/// </summary>
public sealed class RateLimitPartitionTests
{
    // ── Exemptions: infrastructure traffic is never rate-limited ──
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/alive")]
    [InlineData("/.well-known/jwks.json")]
    public void IsRateLimitBypassed_ForInfrastructurePath_ReturnsTrue(string path) =>
        WebApplicationBuilderExtensions.IsRateLimitBypassed(Ctx(path: path)).Should().BeTrue();

    [Fact]
    public void IsRateLimitBypassed_ForGrpcContentType_ReturnsTrue() =>
        WebApplicationBuilderExtensions.IsRateLimitBypassed(Ctx(contentType: "application/grpc")).Should().BeTrue();

    [Fact]
    public void IsRateLimitBypassed_ForRegularApiRequest_ReturnsFalse() =>
        WebApplicationBuilderExtensions.IsRateLimitBypassed(Ctx(path: "/api/events")).Should().BeFalse();

    // ── Partition selection ──
    [Fact]
    public void GlobalRateLimitPartition_ForInfrastructurePath_UsesNoLimiterInfraPartition() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(Ctx(path: "/health"), 300)
            .PartitionKey.Should().Be("__infra");

    [Fact]
    public void GlobalRateLimitPartition_ForAnonymousRequest_UsesNoLimiterAnonymousPartition() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(Ctx(path: "/api/events"), 300)
            .PartitionKey.Should().Be("__anonymous");

    [Fact]
    public void GlobalRateLimitPartition_ForAuthenticatedUser_PartitionsByName() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true, name: "alice"), 300)
            .PartitionKey.Should().Be("alice");

    [Fact]
    public void GlobalRateLimitPartition_WhenNameMissing_FallsBackToUserIdClaim() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true, userId: "u-42"), 300)
            .PartitionKey.Should().Be("u-42");

    [Fact]
    public void GlobalRateLimitPartition_WhenNameAndUserIdMissing_FallsBackToRemoteIp() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true, ip: "10.0.0.5"), 300)
            .PartitionKey.Should().Be("10.0.0.5");

    [Fact]
    public void GlobalRateLimitPartition_WhenNoIdentifyingInfo_FallsBackToConstant() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true), 300)
            .PartitionKey.Should().Be("authenticated");

    private static DefaultHttpContext Ctx(
        string path = "/api/events",
        string? contentType = null,
        bool authenticated = false,
        string? name = null,
        string? userId = null,
        string? ip = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (contentType is not null)
            context.Request.ContentType = contentType;

        if (authenticated)
        {
            var claims = new List<Claim>();
            if (name is not null)
                claims.Add(new Claim(ClaimTypes.Name, name));
            if (userId is not null)
                claims.Add(new Claim("user_id", userId));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
        }

        if (ip is not null)
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);

        return context;
    }
}
