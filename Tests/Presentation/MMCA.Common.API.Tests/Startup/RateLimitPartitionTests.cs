using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.API.Startup;
using MMCA.Common.Shared.Auth;
using Moq;
using StackExchange.Redis;

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

    /// <summary>
    /// SEC-Common-44: the gRPC exemption used to be keyed on the request's own <c>Content-Type</c>,
    /// so an authenticated caller stamping <c>application/grpc</c> on ordinary requests switched off
    /// their 300/min cap entirely. A header is caller-supplied and unverifiable; the routed endpoint
    /// is not.
    /// </summary>
    [Fact]
    public void IsRateLimitBypassed_ForAForgedGrpcContentType_ReturnsFalse() =>
        WebApplicationBuilderExtensions.IsRateLimitBypassed(Ctx(contentType: "application/grpc")).Should().BeFalse();

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

    // M122: the name claim carries the full name, which two users can share; the subject is unique.
    [Fact]
    public void GlobalRateLimitPartition_WithNameAndSubject_PartitionsBySubject() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true, name: "John Smith", userId: "u-42"), 300)
            .PartitionKey.Should().Be("u-42");

    [Fact]
    public void UserPolicyRateLimitPartition_WithNameAndSubject_PartitionsBySubject() =>
        WebApplicationBuilderExtensions.UserPolicyRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true, name: "John Smith", userId: "u-42"),
                new MMCA.Common.API.RateLimiting.RateLimitingSettings())
            .PartitionKey.Should().Be("u-42");

    [Fact]
    public void GlobalRateLimitPartition_WhenNameMissing_FallsBackToUserIdClaim() =>
        WebApplicationBuilderExtensions.GlobalRateLimitPartition(
                Ctx(path: "/api/events", authenticated: true, userId: "u-42"), 300)
            .PartitionKey.Should().Be("u-42");

    // L127: two MMCA apps on one Redis must not share counters, so the Redis key carries the
    // application namespace (the SEC-Common-53 rule the cache keys already follow).
    [Fact]
    public async Task GlobalRateLimitPartition_WhenDistributed_QualifiesTheRedisKeyWithTheApplicationNamespace()
    {
        var keys = new List<string>();
        var database = new Mock<IDatabase>();
        database.Setup(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, long, CommandFlags>((key, _, _) => keys.Add(key.ToString()))
            .ReturnsAsync(1);
        database.Setup(d => d.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database.Object);

        await using var provider = new ServiceCollection()
            .AddSingleton(connection.Object)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Application:Namespace"] = "adc" })
                .Build())
            .BuildServiceProvider();

        var context = Ctx(path: "/api/events", authenticated: true, userId: "u-42");
        context.RequestServices = provider;

        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            context,
            new RateLimitingSettings { Distributed = true, GlobalPermitLimit = 300 });
        await using var limiter = partition.Factory(partition.PartitionKey);
        using var lease = await limiter.AcquireAsync(1, TestContext.Current.CancellationToken);

        keys.Should().ContainSingle().Which.Should().StartWith("rl:adc:global:u-42:");
    }

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

    // ── Per-IP anonymous auth throttle (RateLimitPolicyAuthIp) ──
    [Fact]
    public void AuthIpRateLimitPartition_ForKnownClientIp_PartitionsByIp() =>
        WebApplicationBuilderExtensions.AuthIpRateLimitPartition(Ctx(path: "/Auth/login", ip: "203.0.113.9"), 30)
            .PartitionKey.Should().Be("203.0.113.9");

    // Fails OPEN, not closed. Collapsing unattributable requests into one shared bucket would
    // throttle the in-process TestServer (RemoteIpAddress is null there) and take the whole
    // integration tier down with it, so a null IP must get no limiter at all.
    [Fact]
    public void AuthIpRateLimitPartition_WhenRemoteIpUnknown_UsesNoLimiterPartition() =>
        WebApplicationBuilderExtensions.AuthIpRateLimitPartition(Ctx(path: "/Auth/login"), 30)
            .PartitionKey.Should().Be("__unknown-ip");

    // The policy is anonymous-facing by design: it must partition on IP whether or not a principal
    // is attached, because a password spray carries no identity to partition on.
    [Fact]
    public void AuthIpRateLimitPartition_ForAuthenticatedRequest_StillPartitionsByIp() =>
        WebApplicationBuilderExtensions.AuthIpRateLimitPartition(
                Ctx(path: "/Auth/login", authenticated: true, name: "alice", ip: "198.51.100.4"), 30)
            .PartitionKey.Should().Be("198.51.100.4");

    // Two different sources must never share a bucket, or one spraying host would exhaust the
    // window for everyone else hitting login.
    [Fact]
    public void AuthIpRateLimitPartition_ForDistinctIps_UsesDistinctPartitions()
    {
        var first = WebApplicationBuilderExtensions.AuthIpRateLimitPartition(Ctx(ip: "192.0.2.1"), 30).PartitionKey;
        var second = WebApplicationBuilderExtensions.AuthIpRateLimitPartition(Ctx(ip: "192.0.2.2"), 30).PartitionKey;

        first.Should().NotBe(second);
    }

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
                claims.Add(new Claim(AuthClaimTypes.Subject, userId));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
        }

        if (ip is not null)
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);

        return context;
    }
}
