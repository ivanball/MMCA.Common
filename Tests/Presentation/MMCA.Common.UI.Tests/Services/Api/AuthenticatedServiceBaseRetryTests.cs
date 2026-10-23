using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
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

    // -- L185: a transport failure is method-aware too --
    // The exception arm sees no response, so the API client pipeline (AuthDelegatingHandler) marks the
    // exception with its request's replay safety. A keyless POST or PATCH is then retried only when the
    // failure happened BEFORE the request was sent; the gRPC client applies the same rule.
    [Theory]
    [InlineData("POST", HttpRequestError.ResponseEnded)]
    [InlineData("POST", HttpRequestError.Unknown)]
    [InlineData("PATCH", HttpRequestError.ResponseEnded)]
    [InlineData("PATCH", HttpRequestError.HttpProtocolError)]
    public async Task RetryPolicy_AKeylessPostOrPatchThatFailsAfterSending_IsSentOnce(string method, HttpRequestError error)
    {
        var (attempts, thrown) = await RunThroughApiPipelineAsync(new HttpMethod(method), idempotencyKey: null, error);

        attempts.Should().Be(1, "the server may already have processed it, so a resend could create a duplicate");
        thrown.Should().BeOfType<HttpRequestException>();
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task RetryPolicy_AKeylessPostThatFailsBeforeSending_KeepsItsRetries(HttpRequestError error)
    {
        var (attempts, _) = await RunThroughApiPipelineAsync(HttpMethod.Post, idempotencyKey: null, error);

        attempts.Should().Be(4, "nothing reached the server, so a resend cannot run the write twice");
    }

    [Theory]
    [InlineData("GET", null)]
    [InlineData("PUT", null)]
    [InlineData("POST", "key-1")]
    [InlineData("PATCH", "key-1")]
    public async Task RetryPolicy_AReplaySafeRequestThatFailsAfterSending_KeepsItsRetries(string method, string? idempotencyKey)
    {
        var (attempts, _) = await RunThroughApiPipelineAsync(new HttpMethod(method), idempotencyKey, HttpRequestError.ResponseEnded);

        attempts.Should().Be(4, "an idempotent verb or a keyed write is safe to resend");
    }

    // -- Retry-After on a 429 --
    // A 429 asking for longer than MaxRetryAfterWait is the rate limiter saying "not this window":
    // the refusal surfaces at once instead of three more requests and a held connection.
    [Theory]
    [InlineData(9)]
    [InlineData(60)]
    public async Task RetryPolicy_A429WithRetryAfterOverTheBudget_IsSentOnce(int retryAfterSeconds)
    {
        var backoffCalls = 0;
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ =>
        {
            backoffCalls++;
            return TimeSpan.Zero;
        });
        var attempts = 0;

        using var final = await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds))));
        });

        attempts.Should().Be(1);
        backoffCalls.Should().Be(0);
        final.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RetryPolicy_A429WithADateRetryAfterOverTheBudget_IsSentOnce()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ => TimeSpan.Zero, clock);
        var attempts = 0;

        using var final = await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(TooManyRequests(new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(30))));
        });

        attempts.Should().Be(1);
    }

    // At or under the budget the policy waits exactly what the server asked for, not the backoff.
    [Fact]
    public async Task RetryPolicy_A429WithRetryAfterWithinTheBudget_WaitsThatDelayInsteadOfTheBackoff()
    {
        var backoffCalls = 0;
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ =>
        {
            backoffCalls++;
            return TimeSpan.Zero;
        });
        var attempts = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var final = await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)))
                : new HttpResponseMessage(HttpStatusCode.OK));
        });

        attempts.Should().Be(2);
        final.StatusCode.Should().Be(HttpStatusCode.OK);
        backoffCalls.Should().Be(0, "the server named the delay, so the backoff is not consulted");
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(5)]
    [InlineData(0)]
    public void RetryDelay_ForA429WithinTheBudget_IsTheRetryAfter(int retryAfterSeconds)
    {
        using var response = TooManyRequests(new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds)));

        AuthenticatedServiceBase.RetryDelay(1, response, _ => TimeSpan.FromSeconds(42), TimeProvider.System)
            .Should().Be(TimeSpan.FromSeconds(retryAfterSeconds));
    }

    [Fact]
    public void RetryDelay_ForA429WithADateRetryAfter_MeasuresItAgainstTheClock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        using var response = TooManyRequests(new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(6)));

        AuthenticatedServiceBase.RetryDelay(1, response, _ => TimeSpan.FromSeconds(42), clock)
            .Should().Be(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void RetryDelay_ForA5xxWithRetryAfter_KeepsTheBackoff()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));

        AuthenticatedServiceBase.RetryDelay(2, response, attempt => TimeSpan.FromSeconds(attempt * 10), TimeProvider.System)
            .Should().Be(TimeSpan.FromSeconds(20));
    }

    // A 429 that names no delay keeps today's 2s/4s/8s backoff and all three retries.
    [Fact]
    public async Task RetryPolicy_A429WithoutRetryAfter_KeepsTheBackoff()
    {
        var backoffAttempts = new List<int>();
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(attempt =>
        {
            backoffAttempts.Add(attempt);
            return TimeSpan.Zero;
        });
        var attempts = 0;

        using var final = await policy.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult(TooManyRequests(retryAfter: null));
        });

        attempts.Should().Be(4);
        backoffAttempts.Should().Equal(1, 2, 3);
    }

    private static HttpResponseMessage TooManyRequests(RetryConditionHeaderValue? retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.example.com/events/1")),
        };
        response.Headers.RetryAfter = retryAfter;
        return response;
    }

    private static async Task<(int Attempts, Exception? Thrown)> RunThroughApiPipelineAsync(
        HttpMethod method,
        string? idempotencyKey,
        HttpRequestError error)
    {
        var transport = new ThrowingHandler(error);
        using var client = new HttpClient(new MMCA.Common.UI.Services.Auth.AuthDelegatingHandler(
            Moq.Mock.Of<MMCA.Common.UI.Services.Auth.Tokens.ITokenStorageService>())
        {
            InnerHandler = transport,
        });
        var policy = AuthenticatedServiceBase.BuildRetryPolicy(_ => TimeSpan.Zero);

        Exception? thrown = null;
        try
        {
            using var response = await policy.ExecuteAsync(() =>
            {
                var request = new HttpRequestMessage(method, new Uri("https://api.example.com/events/1/refresh"));
                if (idempotencyKey is not null)
                {
                    request.Headers.Add("Idempotency-Key", idempotencyKey);
                }

                return client.SendAsync(request);
            });
        }
        catch (HttpRequestException ex)
        {
            thrown = ex;
        }

        return (transport.Attempts, thrown);
    }

    private sealed class ThrowingHandler(HttpRequestError error) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new HttpRequestException(error, "transport failure");
        }
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
