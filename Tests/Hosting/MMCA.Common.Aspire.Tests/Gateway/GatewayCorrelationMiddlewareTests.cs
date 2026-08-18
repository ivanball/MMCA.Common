using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MMCA.Common.Aspire.Gateway;

namespace MMCA.Common.Aspire.Tests.Gateway;

/// <summary>
/// Unit tests for <see cref="GatewayCorrelationMiddleware"/>: the edge always ends up with a
/// correlation ID on the request AND on the response, a caller-supplied ID is preserved end to end,
/// and the middleware needs nothing from DI (which is the whole reason it exists separately from
/// the context-bound <c>CorrelationIdMiddleware</c> in MMCA.Common.API).
/// </summary>
public sealed class GatewayCorrelationMiddlewareTests
{
    private const string Header = GatewayCorrelationMiddleware.HeaderName;

    /// <summary>
    /// Runs the middleware and then fires the response's OnStarting callbacks, which is the moment
    /// a real server would write the headers. <see cref="DefaultHttpContext"/>'s built-in response
    /// feature implements <c>OnStarting</c> as a no-op, so the echo is untestable without a feature
    /// that actually records the callbacks.
    /// </summary>
    private static async Task<DefaultHttpContext> RunAsync(Action<DefaultHttpContext>? arrange = null)
    {
        var context = new DefaultHttpContext();
        var responseFeature = new RecordingHttpResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);

        arrange?.Invoke(context);

        var middleware = new GatewayCorrelationMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();

        return context;
    }

    [Fact]
    public async Task InvokeAsync_WithoutHeader_StampsRequestAndEchoesResponse()
    {
        var context = await RunAsync();

        var stamped = context.Request.Headers[Header].ToString();
        stamped.Should().NotBeNullOrWhiteSpace(
            because: "the proxied request must carry an ID downstream so the service adopts it instead of minting a second one");
        context.Response.Headers[Header].ToString().Should().Be(stamped);
    }

    [Fact]
    public async Task InvokeAsync_WithCallerSuppliedHeader_PreservesAndEchoesIt()
    {
        const string supplied = "caller-supplied-id";

        var context = await RunAsync(c => c.Request.Headers[Header] = supplied);

        context.Request.Headers[Header].ToString().Should().Be(supplied);
        context.Response.Headers[Header].ToString().Should().Be(supplied);
    }

    [Fact]
    public async Task InvokeAsync_WithoutHeader_PrefersTheCurrentActivityTraceId()
    {
        var activity = new Activity("gateway-request");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();

        try
        {
            var context = await RunAsync();

            context.Request.Headers[Header].ToString().Should().Be(
                activity.TraceId.ToString(),
                because: "aligning the correlation ID with the W3C trace id is what makes the two searchable together");
        }
        finally
        {
            activity.Stop();
            activity.Dispose();
        }
    }

    [Fact]
    public async Task InvokeAsync_WithBlankHeader_ReplacesItRatherThanEchoingBlank()
    {
        var context = await RunAsync(c => c.Request.Headers[Header] = "   ");

        context.Request.Headers[Header].ToString().Should().NotBeNullOrWhiteSpace();
        context.Response.Headers[Header].ToString().Should().NotBeNullOrWhiteSpace();
    }

    // The load-bearing difference from CorrelationIdMiddleware: nothing is resolved from DI, so
    // this runs in a bare YARP host that never registered the Common application services.
    [Fact]
    public async Task InvokeAsync_ResolvesNothingFromTheContainer()
    {
        var context = new DefaultHttpContext { RequestServices = null! };

        var middleware = new GatewayCorrelationMiddleware(_ => Task.CompletedTask);
        var act = async () => await middleware.InvokeAsync(context);

        await act.Should().NotThrowAsync();
    }

    private sealed class RecordingHttpResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state)
        {
            // Not exercised by these tests.
        }

        public async Task FireOnStartingAsync()
        {
            HasStarted = true;
            foreach (var (callback, state) in _onStarting)
            {
                await callback(state);
            }
        }
    }
}
