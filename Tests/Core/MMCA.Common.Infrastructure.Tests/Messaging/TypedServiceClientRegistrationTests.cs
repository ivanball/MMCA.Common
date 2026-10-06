using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace MMCA.Common.Infrastructure.Tests.Messaging;

/// <summary>
/// L58: <c>AddTypedServiceClient</c> configures its standard resilience handler from the shared
/// <c>HttpResilienceDefaults</c> (the values the Aspire host defaults apply), not the library defaults.
/// </summary>
public sealed class TypedServiceClientRegistrationTests
{
    [Fact]
    public void AddTypedServiceClient_ConfiguresTheResilienceHandlerFromTheSharedDefaults()
    {
        var services = new ServiceCollection();
        services.AddTypedServiceClient<IFakeContract, FakeContract>("identity");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{nameof(IFakeContract)}-standard");

        options.Retry.MaxRetryAttempts.Should().Be(1);
        options.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        options.TotalRequestTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(90));
        options.CircuitBreaker.SamplingDuration.Should().Be(TimeSpan.FromSeconds(60));
    }

    // M185: the inner handler must refuse a POST or PATCH replay, exactly as the host defaults do.
    [Theory]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    [InlineData("GET", true)]
    public async Task AddTypedServiceClient_DoesNotRetryPostOrPatch(string method, bool expectedToRetry)
    {
        var services = new ServiceCollection();
        services.AddTypedServiceClient<IFakeContract, FakeContract>("identity");
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get($"{nameof(IFakeContract)}-standard");

        using var request = new HttpRequestMessage(new HttpMethod(method), "http://identity/x");
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
        var context = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
        try
        {
            context.SetRequestMessage(request);
            var args = new RetryPredicateArguments<HttpResponseMessage>(context, Outcome.FromResult(response), 0);

            (await options.Retry.ShouldHandle(args)).Should().Be(expectedToRetry);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>A contract the typed client is registered for.</summary>
    public interface IFakeContract;

    /// <summary>The typed client implementation, taking its <see cref="HttpClient"/>.</summary>
    /// <param name="httpClient">The configured client.</param>
    public sealed class FakeContract(HttpClient httpClient) : IFakeContract
    {
        /// <summary>Gets the configured client.</summary>
        public HttpClient Client => httpClient;
    }
}
