using System.Diagnostics.Metrics;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Scheduling;

namespace MMCA.Common.Infrastructure.Tests.Scheduling;

/// <summary>
/// Coverage for <see cref="SchedulerMetrics"/>: the meter name a host registers for export, and the
/// instrument names, units and tags an operator builds dashboards on. These are a published contract
/// (the Aspire service defaults add the meter by literal name), so a rename must fail here rather
/// than silently blank a dashboard.
/// </summary>
public sealed class SchedulerMetricsTests
{
    [Fact]
    public void MeterName_MatchesTheNameTheAspireDefaultsRegister() =>
        SchedulerMetrics.MeterName.Should().Be("MMCA.Common.Scheduler");

    [Fact]
    public void Instruments_CarryTheirPublishedNamesAndUnits()
    {
        SchedulerMetrics.RunCounter.Name.Should().Be("scheduler.job.runs");
        SchedulerMetrics.RunCounter.Unit.Should().Be("runs");

        SchedulerMetrics.DurationHistogram.Name.Should().Be("scheduler.job.duration");
        SchedulerMetrics.DurationHistogram.Unit.Should().Be("s");

        SchedulerMetrics.LagHistogram.Name.Should().Be("scheduler.job.lag");
        SchedulerMetrics.LagHistogram.Unit.Should().Be("s");
    }

    [Fact]
    public void Instruments_AllBelongToTheOneSchedulerMeter()
    {
        SchedulerMetrics.RunCounter.Meter.Name.Should().Be(SchedulerMetrics.MeterName);
        SchedulerMetrics.DurationHistogram.Meter.Name.Should().Be(SchedulerMetrics.MeterName);
        SchedulerMetrics.LagHistogram.Meter.Name.Should().Be(SchedulerMetrics.MeterName);
    }

    [Fact]
    public void RunCounter_RecordsTheJobAndOutcomeTags()
    {
        // A job name unique to this test: the runner tests record on the same counter, and xUnit runs
        // test classes in parallel, so the assertion filters to measurements this test produced.
        const string jobName = "metrics-contract-probe";

        // Touch the instrument BEFORE starting the listener so its static initializer has run: a
        // listener started first would be handed the instrument while the field it is compared
        // against is still null.
        var counter = SchedulerMetrics.RunCounter;
        var observed = new List<(long Value, string? Job, string? Outcome)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, counter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? job = null;
            string? outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (string.Equals(tag.Key, "job", StringComparison.Ordinal))
                {
                    job = tag.Value as string;
                }
                else if (string.Equals(tag.Key, "outcome", StringComparison.Ordinal))
                {
                    outcome = tag.Value as string;
                }
            }

            observed.Add((value, job, outcome));
        });
        listener.Start();

        counter.Add(
            1,
            new KeyValuePair<string, object?>("job", jobName),
            new KeyValuePair<string, object?>("outcome", "Succeeded"));

        observed.Where(m => string.Equals(m.Job, jobName, StringComparison.Ordinal))
            .Should().ContainSingle()
            .Which.Should().Be((1L, jobName, "Succeeded"));
    }
}
