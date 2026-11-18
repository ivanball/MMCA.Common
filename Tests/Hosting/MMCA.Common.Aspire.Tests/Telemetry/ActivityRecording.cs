using System.Diagnostics;

namespace MMCA.Common.Aspire.Tests.Telemetry;

/// <summary>
/// The listener and Recorded-flag check shared by the span filter processor tests.
/// </summary>
internal static class ActivityRecording
{
    /// <summary>
    /// Starts an <see cref="ActivityListener"/> for the named sources that always samples, so what a
    /// processor does to the Recorded flag is the only thing the assertions can be reading. The
    /// caller owns the returned listener and disposes it.
    /// </summary>
    public static ActivityListener ListenTo(params string[] sourceNames)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => sourceNames.Contains(source.Name, StringComparer.Ordinal),
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public static bool IsRecorded(Activity activity) =>
        activity.ActivityTraceFlags.HasFlag(ActivityTraceFlags.Recorded);
}
