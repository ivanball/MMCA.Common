using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;
using Polly.Retry;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// <see cref="IdempotentReadRetry"/> gives a service that does not derive from
/// <see cref="AuthenticatedServiceBase"/> (ADC's anonymous <c>SpeakerLookupService</c>,
/// <c>EventLookupService</c>, <c>SessionLookupService</c>, <c>CategoryItemLookupService</c>) the
/// same transient-failure retry the authenticated services get, instead of the single attempt they
/// make today. It is the SAME policy instance, not a copy, and it only ever issues a GET.
/// </summary>
public sealed class IdempotentReadRetryTests
{
    private static readonly Uri RequestUri = new("speakers/paged?pageNumber=1", UriKind.Relative);

    [Fact]
    public void Policy_IsTheExactInstanceAuthenticatedServiceBaseUses() =>
        IdempotentReadRetry.Policy.Should().BeSameAs(PolicyProbe.Inherited);

    [Fact]
    public async Task GetAsync_RetriesATransientFailureAndHandsTheCallerTheFinalResponse()
    {
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        using var handler = new StubHandler(_ => new HttpResponseMessage(statuses.Dequeue()));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };

        using HttpResponseMessage response = await IdempotentReadRetry.GetAsync(
            client, RequestUri, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Should().HaveCount(2, "one 503 is retried once before the 200 ends the run");
    }

    [Fact]
    public async Task GetAsync_DoesNotRetryAPermanentAnswer()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };

        using HttpResponseMessage response = await IdempotentReadRetry.GetAsync(
            client, RequestUri, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAsync_OnlyEverIssuesAGet()
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };

        using HttpResponseMessage response = await IdempotentReadRetry.GetAsync(
            client, RequestUri, TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/speakers/paged?pageNumber=1");
    }

    [Fact]
    public async Task GetAsync_NullClient_Throws()
    {
        var act = () => IdempotentReadRetry.GetAsync(null!, RequestUri, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class PolicyProbe(IHttpClientFactory httpClientFactory, ITokenStorageService tokenStorageService)
        : AuthenticatedServiceBase(httpClientFactory, tokenStorageService)
    {
        // Never instantiated: the probe exists only to read the protected static.
        public static AsyncRetryPolicy<HttpResponseMessage> Inherited => RetryPolicy;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
