using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services;

namespace MMCA.Common.UI.Tests.Services;

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
