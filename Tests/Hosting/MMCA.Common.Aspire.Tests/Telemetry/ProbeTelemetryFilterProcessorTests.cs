using System.Diagnostics;
using AwesomeAssertions;
using MMCA.Common.Aspire.Telemetry;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// Unit tests for <see cref="ProbeTelemetryFilterProcessor"/>: the dependency spans hanging off a
/// health-probe request (the health check's SQL <c>SELECT 1</c>, the Redis PING, the gateway's
/// HttpClient call to a backend's <c>/alive</c>) must be unrecorded so exporters skip them, while
/// the children of a real request keep flowing.
/// </summary>
public sealed class ProbeTelemetryFilterProcessorTests : IDisposable
{
    private const string SourceName = "Test.ProbeTelemetry";
    private const string DependencySourceName = "Test.ProbeTelemetry.Dependency";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivitySource _dependencySource = new(DependencySourceName);
    private readonly ActivityListener _listener;
    private readonly ProbeTelemetryFilterProcessor _sut = new();

    public ProbeTelemetryFilterProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, SourceName, StringComparison.Ordinal)
                || string.Equals(source.Name, DependencySourceName, StringComparison.Ordinal),

            // Always sample, so what the processor does to the Recorded flag is the only thing the
            // assertions can be reading.
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
        _dependencySource.Dispose();
        _sut.Dispose();
    }

    private static bool IsRecorded(Activity activity) =>
        activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded);

    private Activity StartProbeRequest(string urlPath = "/alive")
        => _source.StartActivity(
            "GET",
            ActivityKind.Server,
            default(ActivityContext),
            [new KeyValuePair<string, object?>("url.path", urlPath)])!;

    [Theory]
    [InlineData("/alive")]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public void ChildOfProbeRequest_IsUnrecorded(string urlPath)
    {
        using var request = StartProbeRequest(urlPath);
        using var sqlChild = _dependencySource.StartActivity("SELECT 1", ActivityKind.Client);
        sqlChild.Should().NotBeNull();

        _sut.OnStart(sqlChild);

        IsRecorded(sqlChild).Should().BeFalse("probe dependency spans must be suppressed from export");
        sqlChild.IsAllDataRequested.Should().BeFalse("nothing should enrich a span that is not exported");
    }

    [Fact]
    public void ChildOfNormalRequest_StaysRecorded()
    {
        using var request = StartProbeRequest("/api/tickets");
        using var sqlChild = _dependencySource.StartActivity("SELECT Tickets", ActivityKind.Client);
        sqlChild.Should().NotBeNull();

        _sut.OnStart(sqlChild);

        IsRecorded(sqlChild).Should().BeTrue("real request dependencies keep flowing");
        sqlChild.IsAllDataRequested.Should().BeTrue();
    }

    [Fact]
    public void GrandchildOfProbeRequest_IsUnrecorded()
    {
        using var request = StartProbeRequest();
        using var handler = _dependencySource.StartActivity("HTTP GET", ActivityKind.Client);
        using var dns = _dependencySource.StartActivity("DNS", ActivityKind.Internal);
        dns.Should().NotBeNull();

        _sut.OnStart(dns);

        IsRecorded(dns).Should().BeFalse("the full parent chain is walked");
    }

    [Fact]
    public void ProbeRequestItself_IsUnrecorded()
    {
        using var request = StartProbeRequest();

        _sut.OnStart(request);

        IsRecorded(request).Should().BeFalse();
    }

    [Fact]
    public void ChildOfMarkedProbeRequest_IsUnrecorded()
    {
        // The shape a live host actually produces: the inbound filter refuses the request before the
        // instrumentation writes url.path, and leaves only its marker tag behind.
        using var request = _source.StartActivity("GET", ActivityKind.Server);
        request.Should().NotBeNull();
        request.SetTag(ProbeTelemetryFilter.ProbeMarkerTagName, true);

        using var redisChild = _dependencySource.StartActivity("PING", ActivityKind.Client);
        redisChild.Should().NotBeNull();

        _sut.OnStart(redisChild);

        IsRecorded(redisChild).Should().BeFalse();
    }

    [Fact]
    public void ChildOfProbeRequestKnownOnlyByRoute_IsUnrecorded()
    {
        using var request = _source.StartActivity(
            "GET /alive",
            ActivityKind.Server,
            default(ActivityContext),
            [new KeyValuePair<string, object?>("http.route", "/alive")]);
        using var sqlChild = _dependencySource.StartActivity("SELECT 1", ActivityKind.Client);
        sqlChild.Should().NotBeNull();

        _sut.OnStart(sqlChild);

        IsRecorded(sqlChild).Should().BeFalse();
    }

    [Fact]
    public void ChildOfProbeRequestKnownOnlyByDisplayName_IsUnrecorded()
    {
        using var request = _source.StartActivity("GET /health/ready", ActivityKind.Server);
        using var sqlChild = _dependencySource.StartActivity("SELECT 1", ActivityKind.Client);
        sqlChild.Should().NotBeNull();

        _sut.OnStart(sqlChild);

        IsRecorded(sqlChild).Should().BeFalse("the renamed span is the last probe evidence left");
    }

    [Fact]
    public void ClientSpanToAProbeEndpoint_StaysRecorded()
    {
        // Outgoing probe calls are dropped by the instrumentation filter instead. Matching them here
        // too would cost a real request its whole subtree the moment one dependency looked probe-like.
        using var client = _dependencySource.StartActivity(
            "GET",
            ActivityKind.Client,
            default(ActivityContext),
            [new KeyValuePair<string, object?>("url.path", "/alive")]);
        client.Should().NotBeNull();

        _sut.OnStart(client);

        IsRecorded(client).Should().BeTrue("only server spans identify a probe request");
    }

    [Fact]
    public void UnrelatedRootSpan_StaysRecorded()
    {
        using var unrelated = _dependencySource.StartActivity("BackgroundWork");
        unrelated.Should().NotBeNull();

        _sut.OnStart(unrelated);
        _sut.OnEnd(unrelated);

        IsRecorded(unrelated).Should().BeTrue();
    }

    [Fact]
    public void OnEnd_SuppressesASpanWhoseTagsArrivedAfterStart()
    {
        using var request = _source.StartActivity("GET", ActivityKind.Server);
        using var sqlChild = _dependencySource.StartActivity("SELECT 1", ActivityKind.Client);
        sqlChild.Should().NotBeNull();

        // Nothing identifies the parent as a probe yet, so the start pass leaves the child alone.
        _sut.OnStart(sqlChild);
        IsRecorded(sqlChild).Should().BeTrue();

        request!.SetTag("url.path", "/health");
        _sut.OnEnd(sqlChild);

        IsRecorded(sqlChild).Should().BeFalse("the end pass is the backstop for late tags");
    }

    [Fact]
    public void NullActivity_DoesNotThrow()
    {
        Action start = () => _sut.OnStart(null!);
        Action end = () => _sut.OnEnd(null!);

        start.Should().NotThrow("telemetry callbacks must never throw");
        end.Should().NotThrow("telemetry callbacks must never throw");
    }
}
