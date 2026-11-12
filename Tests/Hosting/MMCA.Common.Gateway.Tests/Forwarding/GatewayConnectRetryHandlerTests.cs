using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Gateway.Forwarding;

namespace MMCA.Common.Gateway.Tests.Forwarding;

/// <summary>
/// Item 31: the opt-in connect-failure retry. Only a body-less GET or HEAD whose attempt never reached
/// the destination (name resolution, connection or TLS failure) is retried, at most
/// <see cref="GatewayRetrySettings.MaxRetryAttempts"/> times, each after
/// <see cref="GatewayRetrySettings.Delay"/> on the injected clock; anything else passes through
/// untouched, and a spent budget rethrows the last exception unchanged so YARP still answers 502.
/// </summary>
public sealed class GatewayConnectRetryHandlerTests
{
    private const string DestinationUri = "http://catalog.internal:8080/api/items?secret=do-not-log";

    [Fact]
    public async Task Get_ConnectionError_IsRetriedOnce_ThenSucceeds()
    {
        var inner = new ScriptedHandler(
            _ => throw ConnectFailure(HttpRequestError.ConnectionError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 1));
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Head_NameResolutionError_IsRetried()
    {
        var inner = new ScriptedHandler(
            _ => throw ConnectFailure(HttpRequestError.NameResolutionError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 1));
        using var request = new HttpRequestMessage(HttpMethod.Head, DestinationUri);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Get_SecureConnectionError_IsRetried()
    {
        var inner = new ScriptedHandler(
            _ => throw ConnectFailure(HttpRequestError.SecureConnectionError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 1));
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Post_ConnectionError_IsNeverRetried()
    {
        var failure = ConnectFailure(HttpRequestError.ConnectionError);
        var inner = new ScriptedHandler(_ => throw failure, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 3));

        // No body at all: the method alone rules the request out.
        using var request = new HttpRequestMessage(HttpMethod.Post, DestinationUri);

        var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetWithContent_IsNeverRetried()
    {
        var failure = ConnectFailure(HttpRequestError.ConnectionError);
        var inner = new ScriptedHandler(_ => throw failure, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 3));
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri)
        {
            Content = new StringContent("a body a replay could not resend"),
        };

        var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ResponseError_IsNeverRetried()
    {
        var inner = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 3));
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task OtherHttpRequestError_IsNeverRetried()
    {
        // The destination accepted the connection and then dropped the response: it may have acted
        // on the request, so it is not a connect failure.
        var failure = ConnectFailure(HttpRequestError.ResponseEnded);
        var inner = new ScriptedHandler(_ => throw failure, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 3));
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_IsNeverRetried()
    {
        // An OperationCanceledException from the attempt itself.
        var canceled = new OperationCanceledException("client went away");
        var innerCanceled = new ScriptedHandler(_ => throw canceled, _ => new HttpResponseMessage(HttpStatusCode.OK));
        using (var invoker = Invoker(innerCanceled, Settings(maxRetryAttempts: 3)))
        using (var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri))
        {
            var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

            await act.Should().ThrowAsync<OperationCanceledException>();
            innerCanceled.Calls.Should().Be(1);
        }

        // DESIGN-QUESTION: rules 7 and 9 do not say what surfaces when the token is cancelled after a
        // retryable failure. A connect failure that arrives after the caller's token was cancelled. Which exception
        // surfaces (the connect failure, or a cancellation from the skipped delay) is the
        // implementer's choice; that no second attempt is made is the rule.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var innerAfterCancel = new ScriptedHandler(
            _ =>
            {
                cts.Cancel();
                throw ConnectFailure(HttpRequestError.ConnectionError);
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var cancelInvoker = Invoker(innerAfterCancel, Settings(maxRetryAttempts: 3));
        using var cancelRequest = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        var actAfterCancel = () => cancelInvoker.SendAsync(cancelRequest, cts.Token);

        await actAfterCancel.Should().ThrowAsync<Exception>();
        innerAfterCancel.Calls.Should().Be(1);
    }

    [Fact]
    public async Task BudgetSpent_RethrowsTheLastException()
    {
        var first = ConnectFailure(HttpRequestError.ConnectionError);
        var second = ConnectFailure(HttpRequestError.NameResolutionError);
        var third = ConnectFailure(HttpRequestError.ConnectionError);
        var inner = new ScriptedHandler(
            _ => throw first,
            _ => throw second,
            _ => throw third,
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 2));
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        var act = () => invoker.SendAsync(request, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(third);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Delay_IsWaitedOnTheTimeProvider()
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var time = new FakeTimeProvider();
        var inner = new ScriptedHandler(
            _ => throw ConnectFailure(HttpRequestError.ConnectionError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = Invoker(inner, Settings(maxRetryAttempts: 1, delay), time);
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        var sending = invoker.SendAsync(request, TestContext.Current.CancellationToken);
        SpinWait.SpinUntil(() => inner.Calls >= 1, TimeSpan.FromSeconds(5)).Should().BeTrue();

        time.Advance(delay - TimeSpan.FromMilliseconds(1));
        inner.Calls.Should().Be(1, "the retry must wait the whole delay on the injected clock");
        sending.IsCompleted.Should().BeFalse();

        time.Advance(TimeSpan.FromMilliseconds(1));
        using var response = await sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(2);
    }

    // Rule 10, not named in the design's test list: one Warning per retry, carrying the method, the
    // destination authority and the HttpRequestError, never the query string.
    [Fact]
    public async Task EachRetry_LogsOneWarning_WithoutTheQueryString()
    {
        var logger = new RecordingLogger();
        var inner = new ScriptedHandler(
            _ => throw ConnectFailure(HttpRequestError.ConnectionError),
            _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(
            new GatewayConnectRetryHandler(Settings(maxRetryAttempts: 1), new FakeTimeProvider(), logger)
            {
                InnerHandler = inner,
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, DestinationUri);

        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Text.Should().Contain("GET")
            .And.Contain("catalog.internal:8080")
            .And.Contain(nameof(HttpRequestError.ConnectionError))
            .And.NotContain("secret")
            .And.NotContain("do-not-log");
    }

    private static GatewayRetrySettings Settings(int maxRetryAttempts, TimeSpan? delay = null) => new()
    {
        Enabled = true,
        MaxRetryAttempts = maxRetryAttempts,

        // Zero unless the test is about the delay, so no test depends on advancing a clock it is not about.
        Delay = delay ?? TimeSpan.Zero,
    };

    private static HttpMessageInvoker Invoker(HttpMessageHandler inner, GatewayRetrySettings settings, TimeProvider? time = null) =>
        new(new GatewayConnectRetryHandler(settings, time ?? new FakeTimeProvider(), NullLogger.Instance)
        {
            InnerHandler = inner,
        });

    private static HttpRequestException ConnectFailure(HttpRequestError error) =>
        new(error, $"simulated {error}");

    /// <summary>An inner handler that plays one scripted step per attempt and counts the attempts.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            var step = script[Math.Min(index, script.Length - 1)];
            try
            {
                return Task.FromResult(step(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }

    /// <summary>A logger that keeps every formatted entry, state included.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception) + " " + values));
            }
        }
    }
}
