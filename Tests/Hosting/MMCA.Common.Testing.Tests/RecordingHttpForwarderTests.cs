using System.Globalization;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Unit tests for the shipped <see cref="RecordingHttpForwarder"/>: the gateway test fake that echoes
/// the destination, the forwarder budget and the outbound trace headers into response headers instead
/// of proxying. The echoes are what every consumer gateway route test asserts on, so a silent change
/// to a header name or an unset marker would break those suites in three repos at once.
/// </summary>
public sealed class RecordingHttpForwarderTests
{
    private const string DestinationPrefix = "http://conference";

    [Fact]
    public async Task SendAsync_EchoesDestinationAndForwarderBudget()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var config = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(100),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        // Act
        var error = await new RecordingHttpForwarder().SendAsync(
            context, DestinationPrefix, NoopInvoker, config, new StampingTransformer(), TestContext.Current.CancellationToken);

        // Assert
        error.Should().Be(ForwarderError.None);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        Header(context, RecordingHttpForwarder.DestinationHeader).Should().Be(DestinationPrefix);
        Header(context, RecordingHttpForwarder.ActivityTimeoutHeader)
            .Should().Be(TimeSpan.FromSeconds(100).ToString("c", CultureInfo.InvariantCulture));
        Header(context, RecordingHttpForwarder.VersionHeader).Should().Be("2.0");
        Header(context, RecordingHttpForwarder.VersionPolicyHeader).Should().Be(nameof(HttpVersionPolicy.RequestVersionExact));
    }

    [Fact]
    public async Task SendAsync_EchoesTheUnsetMarker_ForEveryNullableSettingLeftUnset()
    {
        // Arrange: a cluster that declares no forwarder budget at all. The unset marker is what makes
        // "the shared profile merge never reached this cluster" an assertable outcome rather than an
        // empty header a test could pass over.
        var context = new DefaultHttpContext();

        // Act
        await new RecordingHttpForwarder().SendAsync(
            context,
            DestinationPrefix,
            NoopInvoker,
            new ForwarderRequestConfig(),
            new StampingTransformer(),
            TestContext.Current.CancellationToken);

        // Assert
        Header(context, RecordingHttpForwarder.ActivityTimeoutHeader).Should().Be(RecordingHttpForwarder.UnsetValue);
        Header(context, RecordingHttpForwarder.VersionHeader).Should().Be(RecordingHttpForwarder.UnsetValue);
        Header(context, RecordingHttpForwarder.VersionPolicyHeader).Should().Be(RecordingHttpForwarder.UnsetValue);

        // No IReverseProxyFeature on a bare context: the cluster echo degrades to the marker rather
        // than throwing, so a route test that never went through YARP's own middleware still runs.
        Header(context, RecordingHttpForwarder.ClusterHeader).Should().Be(RecordingHttpForwarder.UnsetValue);
    }

    [Fact]
    public async Task SendAsync_RunsTheRealTransformPipeline_AndEchoesTheStampedTraceHeaders()
    {
        // Arrange
        var context = new DefaultHttpContext();

        // Act
        await new RecordingHttpForwarder().SendAsync(
            context,
            DestinationPrefix,
            NoopInvoker,
            new ForwarderRequestConfig(),
            new StampingTransformer("events-route", "conference"),
            TestContext.Current.CancellationToken);

        // Assert: the transforms genuinely ran against the outbound request, which is the only way a
        // test can observe what a downstream would receive without a network hop.
        Header(context, RecordingHttpForwarder.RouteTraceEchoHeader).Should().Be("events-route");
        Header(context, RecordingHttpForwarder.ClusterTraceEchoHeader).Should().Be("conference");
    }

    [Fact]
    public async Task SendAsync_EchoesTheUnsetMarker_WhenTheTransformsStampNoTraceHeaders()
    {
        // Arrange
        var context = new DefaultHttpContext();

        // Act
        await new RecordingHttpForwarder().SendAsync(
            context,
            DestinationPrefix,
            NoopInvoker,
            new ForwarderRequestConfig(),
            new StampingTransformer(),
            TestContext.Current.CancellationToken);

        // Assert
        Header(context, RecordingHttpForwarder.RouteTraceEchoHeader).Should().Be(RecordingHttpForwarder.UnsetValue);
        Header(context, RecordingHttpForwarder.ClusterTraceEchoHeader).Should().Be(RecordingHttpForwarder.UnsetValue);
    }

    [Fact]
    public async Task SendAsync_HonorsCustomTraceHeaderNames()
    {
        // Arrange: a gateway that renamed its trace headers still gets an observable echo.
        var context = new DefaultHttpContext();
        var forwarder = new RecordingHttpForwarder
        {
            RouteTraceHeaderName = "X-Custom-Route",
            ClusterTraceHeaderName = "X-Custom-Cluster",
        };

        // Act
        await forwarder.SendAsync(
            context,
            DestinationPrefix,
            NoopInvoker,
            new ForwarderRequestConfig(),
            new StampingTransformer(routeTrace: "r", clusterTrace: "c", routeHeader: "X-Custom-Route", clusterHeader: "X-Custom-Cluster"),
            TestContext.Current.CancellationToken);

        // Assert
        Header(context, RecordingHttpForwarder.RouteTraceEchoHeader).Should().Be("r");
        Header(context, RecordingHttpForwarder.ClusterTraceEchoHeader).Should().Be("c");
    }

    [Fact]
    public async Task SendAsync_WithoutACancellationToken_BehavesLikeTheTokenOverload()
    {
        // Arrange: YARP's interface carries both overloads and its middleware may call either.
        var context = new DefaultHttpContext();

        // Act
        var error = await new RecordingHttpForwarder().SendAsync(
            context, DestinationPrefix, NoopInvoker, new ForwarderRequestConfig(), new StampingTransformer());

        // Assert
        error.Should().Be(ForwarderError.None);
        Header(context, RecordingHttpForwarder.DestinationHeader).Should().Be(DestinationPrefix);
    }

    private static HttpMessageInvoker NoopInvoker => new(new HttpClientHandler());

    private static string? Header(HttpContext context, string name) =>
        context.Response.Headers.TryGetValue(name, out var values) ? values.ToString() : null;

    /// <summary>
    /// A transformer that stands in for the gateway's real request-transform chain: it stamps the
    /// route and cluster trace headers onto the OUTBOUND request, exactly where the shipped
    /// <c>GatewayTraceHeaderTransformProvider</c> puts them.
    /// </summary>
    private sealed class StampingTransformer(
        string? routeTrace = null,
        string? clusterTrace = null,
        string routeHeader = "X-MMCA-Route",
        string clusterHeader = "X-MMCA-Cluster") : HttpTransformer
    {
        public override ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(proxyRequest);

            if (routeTrace is not null)
            {
                proxyRequest.Headers.TryAddWithoutValidation(routeHeader, routeTrace);
            }

            if (clusterTrace is not null)
            {
                proxyRequest.Headers.TryAddWithoutValidation(clusterHeader, clusterTrace);
            }

            return ValueTask.CompletedTask;
        }
    }
}
