using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.UI.Services.Auth.Devices;

/// <summary>
/// Runs an <see cref="IDeviceMetadataReporter"/> on behalf of the two callers that report: the sign-in
/// path (<see cref="AuthUIService"/>, after every sign-in) and the refresh backfill
/// (<see cref="Tokens.SecureStoreTokenStorageService"/>, once per process after a successful token
/// refresh). Both get the same rules: the wait is capped at <see cref="Timeout"/>, and any failure,
/// cancellation included, is logged and swallowed, so a report never fails or holds up the call it
/// follows.
/// </summary>
internal static partial class DeviceMetadataReporting
{
    /// <summary>
    /// The longest a caller waits on one report. The report follows a request that just succeeded, so it
    /// normally takes a fraction of this; the bound only caps a hung connection.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The backfill state per reporter instance (a singleton by contract, so in practice one per
    /// process). Keyed by instance rather than held in a static flag so the state lives and dies with
    /// the reporter it describes.
    /// </summary>
    private static readonly ConditionalWeakTable<IDeviceMetadataReporter, BackfillGate> Gates = [];

    /// <summary>Reports once, bounded, never throwing.</summary>
    /// <param name="reporter">The reporter to run.</param>
    /// <param name="logger">Logs a failure; optional.</param>
    /// <param name="cancellationToken">The caller's token, linked to the bound.</param>
    /// <returns>True when the reporter reported (or had nothing to report).</returns>
    public static async Task<bool> ReportAsync(IDeviceMetadataReporter reporter, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            // WaitAsync as well as the token: a reporter that ignores cancellation still cannot hold
            // the caller past the bound.
            var reported = await reporter.ReportAsync(timeout.Token).WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
            if (reported)
            {
                // Whatever the caller, a recorded device needs no backfill: rotation carries it forward.
                Gates.GetValue(reporter, static _ => new BackfillGate()).MarkReported();
            }

            return reported;
        }
#pragma warning disable CA1031 // Do not catch general exception types: the device report is best-effort
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (logger is not null)
            {
                LogReportFailed(logger, exception);
            }

            return false;
        }
    }

    /// <summary>
    /// The refresh backfill: reports unless this reporter already reported successfully in this
    /// process, or a report is already running. A failed report leaves the gate open, so the next
    /// successful refresh (one per access-token lifetime) tries again; nothing retries in between.
    /// </summary>
    /// <param name="reporter">The reporter to run.</param>
    /// <param name="logger">Logs a failure; optional.</param>
    /// <returns>A task that completes once the report ran, was skipped, or was abandoned.</returns>
    public static async Task ReportOncePerProcessAsync(IDeviceMetadataReporter reporter, ILogger? logger)
    {
        var gate = Gates.GetValue(reporter, static _ => new BackfillGate());
        if (!gate.TryBegin())
        {
            return;
        }

        try
        {
            await ReportAsync(reporter, logger, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.End();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reporting this device's metadata for the current session failed; the caller is unaffected.")]
    private static partial void LogReportFailed(ILogger logger, Exception exception);

    /// <summary>Whether the backfill is done, and whether one is running now (so joiners skip it).</summary>
    private sealed class BackfillGate
    {
        private int _reported;
        private int _running;

        public bool TryBegin() =>
            Volatile.Read(ref _reported) == 0 && Interlocked.CompareExchange(ref _running, 1, 0) == 0;

        public void End() => Volatile.Write(ref _running, 0);

        public void MarkReported() => Volatile.Write(ref _reported, 1);
    }
}
