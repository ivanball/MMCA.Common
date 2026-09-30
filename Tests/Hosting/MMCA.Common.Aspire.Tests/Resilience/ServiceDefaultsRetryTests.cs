using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace MMCA.Common.Aspire.Tests.Resilience;

/// <summary>
/// Pins the retry predicate <c>AddServiceDefaults()</c> gives every factory HttpClient (L80): one
/// retry for the idempotent verbs, none for POST and PATCH, because an attempt that timed out may
/// still be running server-side and a service-to-service hop carries no idempotency key.
/// </summary>
public sealed class ServiceDefaultsRetryTests
{
    public static TheoryData<string, bool> MethodsAndWhetherA503IsRetried => new()
    {
        { "GET", true },
        { "HEAD", true },
        { "PUT", true },
        { "DELETE", true },
        { "POST", false },
        { "PATCH", false },
    };

    [Theory]
    [MemberData(nameof(MethodsAndWhetherA503IsRetried))]
    public async Task StandardRetry_OnA503_RetriesOnlyTheIdempotentVerbs(string method, bool retried)
    {
        var options = ResolveProbeOptions();

        var handled = await ShouldHandleAsync(
            options,
            new HttpMethod(method),
            Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        handled.Should().Be(retried);
    }

    [Fact]
    public async Task StandardRetry_AfterAnAttemptTimeoutOnAPost_DoesNotReplayIt()
    {
        var options = ResolveProbeOptions();

        var handled = await ShouldHandleAsync(
            options,
            HttpMethod.Post,
            Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException()));

        handled.Should().BeFalse("the timed-out POST may still be executing on the server");
    }

    private static HttpStandardResilienceOptions ResolveProbeOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddServiceDefaults();
        builder.Services.AddHttpClient("probe");
        using var host = builder.Build();

        // ConfigureHttpClientDefaults hands AddStandardResilienceHandler a builder with no client
        // name, so the defaults applied to every client live under the options name "-standard"
        // ("probe-standard" comes back as untouched library defaults).
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get("-standard");
        options.AttemptTimeout.Timeout.Should().Be(
            TimeSpan.FromSeconds(30),
            "the resolved options must be the ones AddServiceDefaults configured, not library defaults");
        return options;
    }

    private static async Task<bool> ShouldHandleAsync(
        HttpStandardResilienceOptions options,
        HttpMethod method,
        Outcome<HttpResponseMessage> outcome)
    {
        var context = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri("http://probe/resource"));
            context.SetRequestMessage(request);
            return await options.Retry.ShouldHandle(
                new RetryPredicateArguments<HttpResponseMessage>(context, outcome, attemptNumber: 0));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
