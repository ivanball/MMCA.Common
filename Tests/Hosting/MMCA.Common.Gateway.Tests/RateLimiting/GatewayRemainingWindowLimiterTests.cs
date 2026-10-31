using System.Net;
using System.Reflection;
using System.Threading.RateLimiting;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Gateway.RateLimiting;
using MMCA.Common.RateLimiting;

namespace MMCA.Common.Gateway.Tests.RateLimiting;

/// <summary>
/// The named route policies answer a mid-window rejection with the time left in the window, not the
/// whole window, and the package's internal limiter copy (it references no MMCA package) behaves
/// exactly like the one source file the API, Aspire and UI.Web packages compile in (linked into this
/// test project), so the two cannot drift.
/// </summary>
public sealed class GatewayRemainingWindowLimiterTests
{
    private static readonly FixedWindowRateLimiterOptions ParityOptions = new()
    {
        Window = TimeSpan.FromSeconds(30),
        PermitLimit = 2,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    };

    private enum Step
    {
        Acquire,
        AcquireAsync,
        Replenish,
    }

    [Fact]
    public async Task Partition_AMidWindowRejection_RetryAfterIsTheTimeLeft_NotTheWindow()
    {
        var policy = new GatewayRoutePolicySettings { PermitLimit = 1, WindowSeconds = 30 };
        var time = new FakeTimeProvider();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");
        var partition = GatewayRoutePolicyExtensions.Partition(context, policy, time);
        await using var limiter = partition.Factory(partition.PartitionKey);
        limiter.Should().BeOfType<GatewayRemainingWindowLimiter>();
        ((ReplenishingRateLimiter)limiter).IsAutoReplenishing.Should().BeFalse(
            "the middleware's partitioned endpoint limiter replenishes every partition on its own timer");

        using var first = limiter.AttemptAcquire();
        time.Advance(TimeSpan.FromSeconds(18));
        using var rejected = limiter.AttemptAcquire();

        first.IsAcquired.Should().BeTrue();
        rejected.IsAcquired.Should().BeFalse();
        rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(12));

        var services = new ServiceCollection();
        services.AddGatewayRoutePolicies(new GatewaySettings
        {
            RateLimiterPolicies = new Dictionary<string, GatewayRoutePolicySettings>(StringComparer.Ordinal)
            {
                ["auth-tight"] = policy,
            },
        });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, CancellationToken.None);

        context.Response.Headers.RetryAfter.ToString().Should().Be("12");
    }

    [Fact]
    public async Task TheCopy_BehavesLikeTheSharedLimiter_ThroughTheSameClockScript()
    {
        (TimeSpan Advance, Step Step)[] script =
        [
            (TimeSpan.Zero, Step.Acquire),
            (TimeSpan.FromSeconds(3), Step.Acquire),
            (TimeSpan.FromSeconds(4), Step.Acquire),
            (TimeSpan.FromSeconds(1), Step.AcquireAsync),
            (TimeSpan.FromSeconds(10), Step.Replenish),
            (TimeSpan.Zero, Step.Acquire),
            (TimeSpan.FromSeconds(15), Step.Acquire),
            (TimeSpan.Zero, Step.Replenish),
            (TimeSpan.FromSeconds(2), Step.Acquire),
            (TimeSpan.FromMilliseconds(250), Step.AcquireAsync),
            (TimeSpan.FromSeconds(29), Step.Acquire),
            (TimeSpan.FromSeconds(40), Step.Acquire),
            (TimeSpan.Zero, Step.Replenish),
            (TimeSpan.FromSeconds(1), Step.Acquire),
        ];

        var sharedTime = new FakeTimeProvider();
        var copyTime = new FakeTimeProvider();
        await using var shared = new RemainingWindowFixedWindowRateLimiter(ParityOptions, sharedTime);
        await using var copy = new GatewayRemainingWindowLimiter(ParityOptions, copyTime);

        var sharedTrace = await RunAsync(shared, sharedTime, script, () => shared.TimeUntilNextWindow());
        var copyTrace = await RunAsync(copy, copyTime, script, () => copy.TimeUntilNextWindow());

        sharedTrace.Should().Contain(entry => entry.StartsWith("acquire:False", StringComparison.Ordinal), "the script must exercise a rejection");
        copyTrace.Should().Equal(sharedTrace);
        (copy.IsAutoReplenishing, copy.ReplenishmentPeriod).Should().Be((shared.IsAutoReplenishing, shared.ReplenishmentPeriod));
    }

    [Fact]
    public void TheCopy_DeclaresTheSameMembersAsTheSharedLimiter() =>
        Signatures(typeof(GatewayRemainingWindowLimiter))
            .Should().Equal(Signatures(typeof(RemainingWindowFixedWindowRateLimiter)));

    private static async Task<List<string>> RunAsync(
        ReplenishingRateLimiter limiter,
        FakeTimeProvider time,
        (TimeSpan Advance, Step Step)[] script,
        Func<TimeSpan> timeUntilNextWindow)
    {
        var trace = new List<string>();
        foreach (var (advance, step) in script)
        {
            time.Advance(advance);
            if (step == Step.Replenish)
            {
                trace.Add($"replenish:{limiter.TryReplenish()}:{timeUntilNextWindow()}");
                continue;
            }

            using var lease = step == Step.Acquire ? limiter.AttemptAcquire() : await limiter.AcquireAsync();
            var hint = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter.ToString() : "none";
            trace.Add($"acquire:{lease.IsAcquired}:{hint}:{timeUntilNextWindow()}");
        }

        return trace;
    }

    private static List<string> Signatures(Type type) =>
        [.. type
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member is MethodBase or PropertyInfo or FieldInfo && !member.Name.Contains('<', StringComparison.Ordinal))
            .Select(member => member switch
            {
                ConstructorInfo ctor => $".ctor({string.Join(',', ctor.GetParameters().Select(p => p.ParameterType.Name))})",
                MethodInfo method => $"{method.ReturnType.Name} {method.Name}({string.Join(',', method.GetParameters().Select(p => p.ParameterType.Name))})",
                PropertyInfo property => $"{property.PropertyType.Name} {property.Name}",
                FieldInfo field => $"{field.FieldType.Name} {field.Name}",
                _ => member.Name,
            })
            .Order(StringComparer.Ordinal)];
}
