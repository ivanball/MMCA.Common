using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Xunit;

namespace MMCA.Common.Grpc.Tests;

/// <summary>
/// Fault-injection (chaos) test for ADR-009 / item C-8: when an outbound dependency keeps failing, the
/// standard resilience handler's circuit breaker must trip and short-circuit further calls (fail fast)
/// instead of hammering the dead dependency. Complements <see cref="ResilienceHandlerTests"/>, which
/// proves the handler is wired; this proves it actually behaves under injected faults.
/// </summary>
public sealed class ResilienceCircuitBreakerFaultInjectionTests
{
    [Fact]
    public async Task StandardResilienceHandler_OpensCircuit_AfterSustainedFailures_AndShortCircuits()
    {
        using var faultyDependency = new CountingFailureHandler();

        var services = new ServiceCollection();
        services
            .AddHttpClient("chaos")
            .ConfigurePrimaryHttpMessageHandler(() => faultyDependency)
            .AddStandardResilienceHandler(options =>
            {
                // Make the breaker deterministic: one attempt per request (retry disabled via an
                // always-false predicate — MaxRetryAttempts must stay >= 1 per options validation) and
                // open after just two sampled failures, so the test needn't issue the default 100 calls.
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                options.CircuitBreaker.MinimumThroughput = 2;
                options.CircuitBreaker.FailureRatio = 0.5;
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(2);
                options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(5);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
            });

        await using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("chaos");

        // Two failing probes fill the sampling window (100% failures >= the 50% ratio) and trip the breaker.
        for (var i = 0; i < 2; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri("http://chaos.local/"));
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        faultyDependency.CallCount.Should().Be(2, "both probe requests reached the dependency");

        // The breaker is now OPEN: the next call must fail fast WITHOUT touching the dependency.
        var shortCircuited = async () => await client.GetAsync(new Uri("http://chaos.local/"));

        (await shortCircuited.Should().ThrowAsync<Exception>())
            .Which.GetType().Name.Should().Contain(
                "BrokenCircuit",
                "an open circuit breaker rejects calls before they reach the dead dependency");
        faultyDependency.CallCount.Should().Be(2, "the open breaker short-circuited the third call");
    }

    /// <summary>An always-failing dependency (HTTP 503) that counts how many calls actually reached it.</summary>
    private sealed class CountingFailureHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
