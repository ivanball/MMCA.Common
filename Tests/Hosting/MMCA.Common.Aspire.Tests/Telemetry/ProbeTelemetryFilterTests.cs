using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Aspire.Telemetry;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The <c>Telemetry:FilterProbeTelemetry</c> cost knob (rubric §31) and the two instrumentation
/// predicates it installs. Health probes (Container Apps liveness/readiness, the gateway's
/// downstream aggregate probes, YARP active health checks, the availability web test) are pure
/// infrastructure chatter and made up every AppRequests row in both production workspaces. The knob
/// defaults to ON, the opposite of the metrics knobs, so an unset key filters.
/// </summary>
public sealed class ProbeTelemetryFilterTests
{
    private static IConfiguration Config(string? value)
    {
        var values = new Dictionary<string, string?>();
        if (value is not null)
        {
            values["Telemetry:FilterProbeTelemetry"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void Absent_FiltersProbeTelemetry()
        => Extensions.IsProbeTelemetryFilterEnabled(Config(null))
            .Should().BeTrue("an unset knob must filter probe telemetry (the default)");

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void ExplicitFalse_KeepsProbeTelemetry(string raw)
        => Extensions.IsProbeTelemetryFilterEnabled(Config(raw)).Should().BeFalse();

    [Theory]
    [InlineData("true")]
    [InlineData("0")] // not a bool literal, so it cannot turn filtering off
    [InlineData("no")] // unparseable
    [InlineData("")] // blank
    public void TrueOrUnparseable_FiltersProbeTelemetry(string raw)
        => Extensions.IsProbeTelemetryFilterEnabled(Config(raw))
            .Should().BeTrue("only an explicit boolean false turns the filter off");

    // ── Inbound requests ──
    [Theory]
    [InlineData("/alive")]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/health/anything-added-later")]
    [InlineData("/Health/Ready")] // routing is case-insensitive
    public void ProbeRequest_IsNotCollected(string path)
        => ProbeTelemetryFilter.ShouldCollectRequest(RequestFor(path)).Should().BeFalse();

    [Theory]
    [InlineData("/")]
    [InlineData("/api/tickets")]
    [InlineData("/healthcare")] // shares the prefix but is not a probe route
    [InlineData("/alive-check")]
    [InlineData("")]
    public void NormalRequest_IsCollected(string path)
        => ProbeTelemetryFilter.ShouldCollectRequest(RequestFor(path)).Should().BeTrue();

    [Fact]
    public void NullContext_IsCollected()
        => ProbeTelemetryFilter.ShouldCollectRequest(null!)
            .Should().BeTrue("telemetry callbacks must never throw or over-filter");

    [Fact]
    public void ProbeRequest_MarksTheCurrentServerActivity()
    {
        using var source = new ActivitySource("Test.ProbeFilter.Marker");
        using var listener = ListenTo(source.Name);

        using var request = source.StartActivity("GET", ActivityKind.Server);
        request.Should().NotBeNull();

        ProbeTelemetryFilter.ShouldCollectRequest(RequestFor("/alive")).Should().BeFalse();

        request.GetTagItem(ProbeTelemetryFilter.ProbeMarkerTagName)
            .Should().NotBeNull("descendant spans recognize their probe ancestor by this marker, "
                + "because a filtered request never gets a url.path tag");
    }

    [Fact]
    public void NormalRequest_DoesNotMarkTheCurrentServerActivity()
    {
        using var source = new ActivitySource("Test.ProbeFilter.NoMarker");
        using var listener = ListenTo(source.Name);

        using var request = source.StartActivity("GET", ActivityKind.Server);
        request.Should().NotBeNull();

        ProbeTelemetryFilter.ShouldCollectRequest(RequestFor("/api/tickets")).Should().BeTrue();

        request.GetTagItem(ProbeTelemetryFilter.ProbeMarkerTagName).Should().BeNull();
    }

    // ── Outbound calls (YARP active health checks, the gateway's downstream probes) ──
    [Theory]
    [InlineData("http://identity/alive")]
    [InlineData("https://conference.internal/health")]
    [InlineData("http://engagement/health/ready")]
    [InlineData("http://identity/alive?probe=1")]
    public void OutgoingProbeCall_IsNotCollected(string url)
        => ProbeTelemetryFilter.ShouldCollectOutgoing(new HttpRequestMessage(HttpMethod.Get, url))
            .Should().BeFalse();

    [Theory]
    [InlineData("/alive")]
    [InlineData("/health/ready?x=1")]
    public void OutgoingProbeCall_OnARelativeUri_IsNotCollected(string url)
        => ProbeTelemetryFilter.ShouldCollectOutgoing(
                new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative)))
            .Should().BeFalse();

    [Theory]
    [InlineData("http://identity/api/users")]
    [InlineData("https://gateway/")]
    [InlineData("http://identity/healthcare")]
    public void NormalOutgoingCall_IsCollected(string url)
        => ProbeTelemetryFilter.ShouldCollectOutgoing(new HttpRequestMessage(HttpMethod.Get, url))
            .Should().BeTrue();

    [Fact]
    public void OutgoingCallWithoutUri_IsCollected()
        => ProbeTelemetryFilter.ShouldCollectOutgoing(new HttpRequestMessage())
            .Should().BeTrue("a request with no URI cannot be identified as a probe");

    private static DefaultHttpContext RequestFor(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }

    private static ActivityListener ListenTo(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
