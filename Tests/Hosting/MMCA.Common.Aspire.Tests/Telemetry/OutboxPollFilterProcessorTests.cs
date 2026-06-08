using System.Diagnostics;
using AwesomeAssertions;
using MMCA.Common.Aspire.Telemetry;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// Unit tests for <see cref="OutboxPollFilterProcessor"/>: outbox poll spans and their
/// descendants (e.g. the SqlClient dependency span) must be unrecorded so exporters skip
/// them, while unrelated spans and real outbox work spans stay recorded.
/// </summary>
public sealed class OutboxPollFilterProcessorTests : IDisposable
{
    private readonly ActivitySource _outboxSource = new("MMCA.Common.Outbox");
    private readonly ActivitySource _otherSource = new("Test.Other");
    private readonly ActivityListener _listener;
    private readonly OutboxPollFilterProcessor _sut = new();

    public OutboxPollFilterProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, "MMCA.Common.Outbox", StringComparison.Ordinal)
                || string.Equals(source.Name, "Test.Other", StringComparison.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _outboxSource.Dispose();
        _otherSource.Dispose();
        _sut.Dispose();
    }

    private static bool IsRecorded(Activity activity) =>
        activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded);

    [Fact]
    public void PollSpan_IsUnrecorded()
    {
        using var poll = _outboxSource.StartActivity("OutboxPoll");
        poll.Should().NotBeNull();

        _sut.OnEnd(poll!);

        IsRecorded(poll!).Should().BeFalse("poll spans must be suppressed from export");
    }

    [Fact]
    public void ChildOfPollSpan_IsUnrecorded()
    {
        // The child simulates the SqlClient dependency span the Azure Monitor distro creates
        // for the poll query — different source, parented to the poll span via Activity.Current.
        using var poll = _outboxSource.StartActivity("OutboxPoll");
        using var sqlChild = _otherSource.StartActivity("SELECT OutboxMessages");
        sqlChild.Should().NotBeNull();

        _sut.OnEnd(sqlChild!);

        IsRecorded(sqlChild!).Should().BeFalse("children of poll spans must be suppressed");
    }

    [Fact]
    public void GrandchildOfPollSpan_IsUnrecorded()
    {
        using var poll = _outboxSource.StartActivity("OutboxPoll");
        using var child = _otherSource.StartActivity("Intermediate");
        using var grandchild = _otherSource.StartActivity("Leaf");
        grandchild.Should().NotBeNull();

        _sut.OnEnd(grandchild!);

        IsRecorded(grandchild!).Should().BeFalse("the full parent chain is walked");
    }

    [Fact]
    public void UnrelatedRootSpan_StaysRecorded()
    {
        using var unrelated = _otherSource.StartActivity("SomeRequest");
        unrelated.Should().NotBeNull();

        _sut.OnEnd(unrelated!);

        IsRecorded(unrelated!).Should().BeTrue();
    }

    [Fact]
    public void SameNameFromDifferentSource_StaysRecorded()
    {
        // A consumer span that happens to be called "OutboxPoll" must not be suppressed.
        using var lookalike = _otherSource.StartActivity("OutboxPoll");
        lookalike.Should().NotBeNull();

        _sut.OnEnd(lookalike!);

        IsRecorded(lookalike!).Should().BeTrue("only the MMCA.Common.Outbox source is filtered");
    }

    [Fact]
    public void OutboxProcessSpan_StaysRecorded()
    {
        // Real outbox work — the per-message processing span — must keep flowing to exporters.
        using var process = _outboxSource.StartActivity("OutboxProcess");
        process.Should().NotBeNull();

        _sut.OnEnd(process!);

        IsRecorded(process!).Should().BeTrue("real outbox work spans are not poll noise");
    }

    [Fact]
    public void NullActivity_DoesNotThrow()
    {
        Action act = () => _sut.OnEnd(null!);

        act.Should().NotThrow("telemetry callbacks must never throw");
    }
}
