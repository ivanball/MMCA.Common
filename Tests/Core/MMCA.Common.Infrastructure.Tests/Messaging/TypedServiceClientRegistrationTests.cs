using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

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
