using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Api;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// The retry policy's disposal contract. Polly hands the caller only the FINAL outcome, so every
/// retried response has to be disposed by the policy itself: under sustained backend 5xx/429, which
/// is exactly when the retries fire, an undisposed attempt keeps its content buffer alive and its
/// connection out of the handler pool until finalization.
/// </summary>
public sealed class AuthenticatedServiceBaseRetryTests
{
    [Fact]
    public async Task RetryPolicy_DisposesEveryRetriedResponseAndLeavesTheFinalOneToTheCaller()
    {
        var attempts = new List<TrackingHttpResponseMessage>
        {
            new(HttpStatusCode.ServiceUnavailable),
            new(HttpStatusCode.ServiceUnavailable),
            new(HttpStatusCode.OK),
        };

        var index = 0;
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ => TimeSpan.Zero);

        HttpResponseMessage final = await policy.ExecuteAsync(() =>
            Task.FromResult<HttpResponseMessage>(attempts[index++]));

        index.Should().Be(3, "two retryable responses are retried and the third ends the run");
        attempts[0].IsDisposed.Should().BeTrue("a retried response is never handed to the caller");
        attempts[1].IsDisposed.Should().BeTrue();
        attempts[2].IsDisposed.Should().BeFalse("the caller owns the final response and its `using`");
        final.Should().BeSameAs(attempts[2]);

        final.Dispose();
    }

    [Fact]
    public async Task RetryPolicy_WhenAnAttemptThrows_HasNoResponseToDispose()
    {
        var responses = new List<TrackingHttpResponseMessage> { new(HttpStatusCode.OK) };
        var attempt = 0;
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ => TimeSpan.Zero);

        HttpResponseMessage final = await policy.ExecuteAsync(() =>
            attempt++ == 0
                ? throw new HttpRequestException("connection reset")
                : Task.FromResult<HttpResponseMessage>(responses[0]));

        // A thrown attempt carries no result, so the onRetry callback must tolerate a null outcome.
        final.Should().BeSameAs(responses[0]);
        responses[0].IsDisposed.Should().BeFalse();

        final.Dispose();
    }

    // -- Non-idempotent writes are not replayed (O-09) --
    [Theory]
    [InlineData("POST", HttpStatusCode.BadGateway)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable)]
    [InlineData("POST", HttpStatusCode.TooManyRequests)]
    [InlineData("PATCH", HttpStatusCode.BadGateway)]
    [InlineData("PATCH", HttpStatusCode.TooManyRequests)]
    public async Task RetryPolicy_APostOrPatchWithoutAnIdempotencyKey_IsSentOnce(string method, HttpStatusCode status)
    {
        var (attempts, final) = await RunAsync(new HttpMethod(method), idempotencyKey: null, status);

        attempts.Should().Be(1, "a retried POST or PATCH without a key is a second request, not a replay");
        final.StatusCode.Should().Be(status, "the caller sees the server's own failure, not a retry's 429");
        final.Dispose();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    public async Task RetryPolicy_APostOrPatchWithAnIdempotencyKey_IsRetried(string method)
    {
        var (attempts, final) = await RunAsync(new HttpMethod(method), idempotencyKey: "key-1", HttpStatusCode.BadGateway);

        attempts.Should().Be(4, "the server deduplicates on the key, so the write is retry-safe");
        final.Dispose();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task RetryPolicy_AnIdempotentVerb_KeepsItsRetries(string method)
    {
        var (attempts, final) = await RunAsync(new HttpMethod(method), idempotencyKey: null, HttpStatusCode.BadGateway);

        attempts.Should().Be(4, "the first attempt plus three retries");
        final.Dispose();
    }

    private static async Task<(int Attempts, HttpResponseMessage Final)> RunAsync(
        HttpMethod method,
        string? idempotencyKey,
        HttpStatusCode status)
    {
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ => TimeSpan.Zero);
        var attempts = 0;

        HttpResponseMessage final = await policy.ExecuteAsync(() =>
        {
            attempts++;
            var request = new HttpRequestMessage(method, new Uri("https://api.example.com/events/1/refresh"));
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            // HttpClient attaches the request (default headers included) to every response it returns.
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        });

        return (attempts, final);
    }

    private sealed class TrackingHttpResponseMessage(HttpStatusCode statusCode)
        : HttpResponseMessage(statusCode)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
