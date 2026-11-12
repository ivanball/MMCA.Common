using System.Diagnostics.Metrics;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

namespace MMCA.Common.Infrastructure.Tests.Persistence.InternalCommands.Processing;

/// <summary>
/// Records every measurement on the <c>MMCA.Common.InternalCommands</c> meter with its tags. A raw
/// <see cref="MeterListener"/>, as the outbox tests use, because the test project does not reference
/// Microsoft.Extensions.Diagnostics.Testing. Observable gauges only produce a value when
/// <see cref="RecordObservableInstruments"/> asks for one.
/// </summary>
internal sealed class InternalCommandMeterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly System.Threading.Lock _gate = new();
    private readonly List<RecordedMeasurement> _measurements = [];

    public InternalCommandMeterRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, InternalCommandMetrics.MeterName, StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>Gets every measurement recorded so far.</summary>
    public IReadOnlyList<RecordedMeasurement> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>Asks every observable gauge on the meter for its current value.</summary>
    public void RecordObservableInstruments() => _listener.RecordObservableInstruments();

    /// <summary>The measurements of one instrument.</summary>
    /// <param name="instrumentName">The instrument name.</param>
    /// <returns>Its measurements, in order.</returns>
    public IReadOnlyList<RecordedMeasurement> For(string instrumentName) =>
        [.. All.Where(m => string.Equals(m.Instrument, instrumentName, StringComparison.Ordinal))];

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new RecordedMeasurement(instrument.Name, value, copy));
        }
    }
}

/// <summary>One measurement with its tags.</summary>
/// <param name="Instrument">The instrument name.</param>
/// <param name="Value">The value, widened to double.</param>
/// <param name="Tags">The tags it carried.</param>
internal sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
{
    /// <summary>The string value of one tag, or null when absent.</summary>
    /// <param name="key">The tag key.</param>
    /// <returns>The tag value as a string, or null.</returns>
    public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value as string : null;
}
