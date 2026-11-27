namespace MMCA.Common.UI.Services.Capabilities.Media;

/// <summary>
/// Tells a continuous scan session (<see cref="IBarcodeScannerService.ScanContinuouslyAsync"/>) whether a
/// decode is the code it just handled, still in view. A camera decodes the code in its frame again and
/// again; without this the check-in loop submitted the same code about once a second while the operator
/// held it up (O-24). Only the most recent payload is remembered, and every sighting of it restarts the
/// window, so a code held in view is handled once and handled again only after it has been out of view
/// for <see cref="RepeatWindow"/>.
/// </summary>
/// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> outside tests.</param>
internal sealed class RepeatScanFilter(TimeProvider timeProvider)
{
    /// <summary>
    /// How long a payload stays a repeat after it was last seen. Long enough to outlast an operator
    /// lowering a badge after the outcome shows (about a second plus the outcome dwell), short enough
    /// that a deliberate second scan of the same code goes through. A constant rather than an option:
    /// it is a property of handheld scanning, not of an app.
    /// </summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(5);

    private string? _lastPayload;
    private long _lastSeen;

    /// <summary>
    /// Records a sighting of <paramref name="payload"/> and reports whether it repeats the previous
    /// payload within <see cref="RepeatWindow"/> of its last sighting.
    /// </summary>
    /// <param name="payload">The decoded payload.</param>
    /// <returns><see langword="true"/> to skip it; <see langword="false"/> to handle it.</returns>
    internal bool IsRepeat(string payload)
    {
        var now = timeProvider.GetTimestamp();
        var repeat = _lastPayload is not null
            && string.Equals(payload, _lastPayload, StringComparison.Ordinal)
            && timeProvider.GetElapsedTime(_lastSeen, now) < RepeatWindow;

        _lastPayload = payload;
        _lastSeen = now;
        return repeat;
    }
}
