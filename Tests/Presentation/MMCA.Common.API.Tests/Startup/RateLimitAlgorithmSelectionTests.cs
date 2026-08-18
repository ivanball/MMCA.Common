using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.API.Startup;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Asserts which limiter each partition factory actually builds. The partition key alone does not
/// say whether a request is counted in memory or against the shared Redis counter, and the
/// difference is the whole point of <see cref="RateLimitingSettings.Distributed"/>, so these tests
/// resolve the partition's factory and inspect the limiter it produces.
/// </summary>
public sealed class RateLimitAlgorithmSelectionTests
{
    [Fact]
    public void GlobalPartition_WithFixedWindowSettings_BuildsAFixedWindowLimiter()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            AuthenticatedContext(), new RateLimitingSettings());

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<FixedWindowRateLimiter>();
    }

    [Fact]
    public void GlobalPartition_WithSlidingWindowSettings_BuildsASlidingWindowLimiter()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            AuthenticatedContext(),
            new RateLimitingSettings { Algorithm = RateLimitAlgorithm.SlidingWindow, SegmentsPerWindow = 6 });

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<SlidingWindowRateLimiter>();
    }

    [Fact]
    public void GlobalPartition_WhenDistributedAndRedisIsPresent_BuildsTheSharedCounterLimiter()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            AuthenticatedContext(withRedis: true),
            new RateLimitingSettings { Distributed = true });

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<RedisFixedWindowRateLimiter>();
    }

    // Turning the flag on before wiring Redis must degrade to per-instance limits rather than
    // failing startup or silently removing the limit altogether.
    [Fact]
    public void GlobalPartition_WhenDistributedButRedisIsAbsent_FallsBackToTheInMemoryLimiter()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            AuthenticatedContext(),
            new RateLimitingSettings { Distributed = true });

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<FixedWindowRateLimiter>();
    }

    [Fact]
    public void UserPolicyPartition_PartitionsByNameAndHonorsTheAlgorithm()
    {
        var partition = WebApplicationBuilderExtensions.UserPolicyRateLimitPartition(
            AuthenticatedContext(),
            new RateLimitingSettings { Algorithm = RateLimitAlgorithm.SlidingWindow });

        partition.PartitionKey.Should().Be("alice");

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<SlidingWindowRateLimiter>();
    }

    [Fact]
    public void UserPolicyPartition_WhenNoIdentity_FallsBackToTheAnonymousBucket() =>
        WebApplicationBuilderExtensions.UserPolicyRateLimitPartition(
                new DefaultHttpContext(), new RateLimitingSettings())
            .PartitionKey.Should().Be("anonymous");

    // The login throttle stays per-instance even with the distributed flag on: per-account lockout
    // already backs it, and a login throttle that fails open on a Redis outage is a worse trade
    // than one that stays local.
    [Fact]
    public void AuthIpPartition_WhenDistributed_StaysInMemory()
    {
        var partition = WebApplicationBuilderExtensions.AuthIpRateLimitPartition(
            AuthenticatedContext(withRedis: true),
            new RateLimitingSettings { Distributed = true });

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<FixedWindowRateLimiter>();
    }

    [Fact]
    public void AuthIpPartition_WithSlidingWindowSettings_BuildsASlidingWindowLimiter()
    {
        var partition = WebApplicationBuilderExtensions.AuthIpRateLimitPartition(
            AuthenticatedContext(),
            new RateLimitingSettings { Algorithm = RateLimitAlgorithm.SlidingWindow });

        using var limiter = ResolveLimiter(partition);

        limiter.Should().BeOfType<SlidingWindowRateLimiter>();
    }

    /// <summary>
    /// Invokes the partition's limiter factory. <c>RateLimitPartition&lt;TKey&gt;.Factory</c> is
    /// internal to the BCL, so it is reached by looking for the only member of the struct whose
    /// value is a limiter factory rather than by hard-coding its name.
    /// </summary>
    private static RateLimiter ResolveLimiter(RateLimitPartition<string> partition)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var factory = typeof(RateLimitPartition<string>).GetProperties(flags)
            .Select(property => property.GetValue(partition))
            .Concat(typeof(RateLimitPartition<string>).GetFields(flags)
                .Select(field => field.GetValue(partition)))
            .OfType<Func<string, RateLimiter>>()
            .FirstOrDefault();

        factory.Should().NotBeNull(
            because: "RateLimitPartition<string> must expose the limiter factory this test resolves the built limiter through");

        return factory(partition.PartitionKey);
    }

    private static DefaultHttpContext AuthenticatedContext(bool withRedis = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/events";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], authenticationType: "TestAuth"));

        var services = new ServiceCollection();
        if (withRedis)
        {
            services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        }

        context.RequestServices = services.BuildServiceProvider();
        return context;
    }
}
