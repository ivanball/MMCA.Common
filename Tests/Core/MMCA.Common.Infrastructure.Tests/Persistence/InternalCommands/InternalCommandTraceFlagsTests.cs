using System.Diagnostics;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Infrastructure.Context;
using MMCA.Common.Infrastructure.Persistence.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands;

/// <summary>
/// The scheduling request's sampling decision rides on the row beside its trace and span ids, and
/// the processor rebuilds the execution span's parent with the STORED flags (ADR-003, Amendment
/// 2026-11-23): under a parent-based sampler the span is recorded only when the scheduling request
/// was sampled, and a row with no stored flags (written before the column existed) reads as not
/// sampled.
/// <para>
/// The listener samples only the trace ids these tests mint, so no other test's activities on the
/// process-wide internal-command source are affected.
/// </para>
/// </summary>
public sealed class InternalCommandTraceFlagsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ActivityTraceFlags.Recorded)]
    [InlineData(ActivityTraceFlags.None)]
    public void Capture_CarriesTheAmbientTraceFlags(ActivityTraceFlags flags)
    {
        using var activity = new Activity("schedule").Start();
        activity.ActivityTraceFlags = flags;

        var origin = Capture();

        origin.TraceId.Should().Be(activity.TraceId.ToString());
        origin.SpanId.Should().Be(activity.SpanId.ToString());
        origin.TraceFlags.Should().Be((byte)flags);
    }

    [Fact]
    public void Capture_CarriesNoTraceFlags_WhenNoActivityIsCurrent()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var origin = Capture();

            origin.TraceId.Should().BeNull();
            origin.TraceFlags.Should().BeNull("no ambient activity means no sampling decision to carry");
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)0)]
    [InlineData(null)]
    public void FromCommand_StoresTheOriginTraceFlags(byte? traceFlags)
    {
        var origin = new InternalCommandOrigin(null, null, null, null, null, "trace", "span", traceFlags);

        var row = InternalCommandMessage.FromCommand(new RecordingCommand("x"), Now, Now, origin);

        row.TraceFlags.Should().Be(traceFlags);
    }

    [Fact]
    public void ExecutionActivity_ForARowFromASampledRequest_IsRecorded()
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var listener = ParentBasedListener(traceId);

        using var activity = InternalCommandProcessor.StartExecutionActivity(
            Row(traceId, (byte)ActivityTraceFlags.Recorded), "source");

        activity.Should().NotBeNull();
        activity!.Recorded.Should().BeTrue("the scheduling request was sampled");
        activity.HasRemoteParent.Should().BeTrue("the parent was rebuilt from the row, not taken from this process");
        activity.TraceId.Should().Be(traceId);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData(null)]
    public void ExecutionActivity_ForARowFromAnUnsampledRequestOrWithNoStoredFlags_IsNotRecorded(byte? traceFlags)
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var listener = ParentBasedListener(traceId);

        using var activity = InternalCommandProcessor.StartExecutionActivity(Row(traceId, traceFlags), "source");

        (activity?.Recorded ?? false).Should().BeFalse(
            "a parent-based sampler drops the execution span of a request it did not sample, so no orphan trace is exported");
    }

    private static InternalCommandMessage Row(ActivityTraceId traceId, byte? traceFlags) =>
        InternalCommandMessage.FromCommand(
            new RecordingCommand("x"),
            Now,
            Now,
            new InternalCommandOrigin(
                null, null, null, null, null, traceId.ToHexString(), ActivitySpanId.CreateRandom().ToHexString(), traceFlags));

    /// <summary>
    /// A parent-based sampler over this test's own trace only, as the OpenTelemetry default sampler
    /// behaves for a remote parent: recorded parent, recorded child; otherwise the activity is created
    /// for propagation alone and is not recorded.
    /// </summary>
    private static ActivityListener ParentBasedListener(ActivityTraceId traceId)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, InternalCommandMetrics.MeterName, StringComparison.Ordinal),
            Sample = (ref options) => (options.Parent.TraceId == traceId, options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)) switch
            {
                (false, _) => ActivitySamplingResult.None,
                (true, true) => ActivitySamplingResult.AllDataAndRecorded,
                (true, false) => ActivitySamplingResult.PropagationData,
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static InternalCommandOrigin Capture()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) } };

        return new InternalCommandOriginCapture(new CurrentUserService(accessor), new TenantContext(), new CorrelationContext()).Capture();
    }
}
