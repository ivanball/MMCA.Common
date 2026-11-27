using System.Runtime.ExceptionServices;
using MMCA.Common.UI.Services.Capabilities.Media;

namespace MMCA.Common.UI.Maui.Capabilities.Media;

/// <summary>
/// MAUI camera scanner over ZXing.Net.MAUI (ADR-042). Pushes <see cref="BarcodeScanPage"/> modally
/// over the current window page, resolves on the first decoded payload (or, for a continuous session,
/// keeps the page open until the session ends), and pops the page again on every exit path. The head
/// declares the camera permission (Android CAMERA, iOS NSCameraUsageDescription) and this service
/// requests it before the page opens, because the camera
/// view of a page opened without it stays black: a denial, or a head that never declared it, reads
/// as <see langword="null"/> with no page shown.
/// <para>
/// Registered by <c>UseCommonBarcodeScanner</c> only, so a head that never scans neither ships
/// the camera handler nor needs the permission.
/// </para>
/// <para>
/// The page's two strings are supplied as delegates, not values, and are invoked once per scan, at
/// the moment the page is built. The service is a singleton created while the app is being built,
/// which is BEFORE <c>MauiCultureInitializer</c> restores the user's persisted language (ADR-027);
/// resolving the text at construction would therefore pin it to the device language for the life of
/// the process, and no later in-app language switch would ever reach it.
/// </para>
/// </summary>
public sealed class MauiBarcodeScannerService : IBarcodeScannerService
{
    private readonly Func<string> _cancelText;
    private readonly Func<string> _cameraDescription;

    /// <summary>
    /// Initializes the service with text resolved lazily, per scan: pass the resource lookups
    /// themselves (for example <c>() =&gt; Localizer["Cancel"]</c>) so each scan page is built under
    /// the <see cref="System.Globalization.CultureInfo.CurrentUICulture"/> in effect at that moment.
    /// </summary>
    /// <param name="cancelText">Resolves the label for the scan page's cancel button.</param>
    /// <param name="cameraDescription">Resolves the accessible description and title for the scan surface.</param>
    public MauiBarcodeScannerService(Func<string> cancelText, Func<string> cameraDescription)
    {
        ArgumentNullException.ThrowIfNull(cancelText);
        ArgumentNullException.ThrowIfNull(cameraDescription);

        _cancelText = cancelText;
        _cameraDescription = cameraDescription;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Android and iOS only. Mac Catalyst and Windows have cameras, but the scan affordance there
    /// is a desktop paste field in every head that uses this, and the ZXing camera view is not a
    /// supported surface on those targets.
    /// </remarks>
    public bool IsSupported =>
        DeviceInfo.Current.Platform == DevicePlatform.Android ||
        DeviceInfo.Current.Platform == DevicePlatform.iOS;

    /// <inheritdoc />
    public async Task<bool> EnsureCameraAccessAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return await MainThread.InvokeOnMainThreadAsync(RequestCameraAsync).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types - a head that never declared the permission throws here, and that must read as "no camera access"
        catch
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureCameraAccessAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            // The delegates are invoked here, once per scan, so the page is built under the culture
            // that is active NOW rather than the one that was active when the app was built.
            return await MainThread
                .InvokeOnMainThreadAsync(() => ScanOnMainThreadAsync(_cancelText(), _cameraDescription(), cancellationToken))
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types - scanning is best-effort; a missing window, a denied camera, and a handler-less platform must all read as "no scan"
        catch
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// One modal page for the whole session, not one per code: on a decode the camera pauses, the
    /// payload goes to <paramref name="onScanned"/> on the main thread, and the text it returns is shown
    /// (and announced to a screen reader) over the camera view, where it stays until the next new code
    /// is decoded. Detection resumes after <see cref="OutcomeDwell"/>. Before, the check-in loop called
    /// <see cref="ScanAsync"/> again the moment the outcome rendered, so the operator never saw it (O-24).
    /// </para>
    /// <para>
    /// The token handed to <paramref name="onScanned"/> is also cancelled when the operator ends the
    /// session. A denied camera, a missing window or a platform fault ends the session quietly, as
    /// <see cref="ScanAsync"/> returns <see langword="null"/> for them; an exception thrown by
    /// <paramref name="onScanned"/> ends it, pops the page, and is rethrown with its original stack.
    /// </para>
    /// </remarks>
    public async Task ScanContinuouslyAsync(
        Func<string, CancellationToken, Task<string?>> onScanned,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onScanned);

        if (!await EnsureCameraAccessAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        ExceptionDispatchInfo? callbackFailure = null;
        try
        {
            callbackFailure = await MainThread
                .InvokeOnMainThreadAsync(() => RunSessionOnMainThreadAsync(_cancelText(), _cameraDescription(), onScanned, cancellationToken))
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types - as for ScanAsync: a missing window, a denied camera, and a handler-less platform must all read as "the session ended"
        catch
#pragma warning restore CA1031
        {
            return;
        }

        callbackFailure?.Throw();
    }

    /// <summary>
    /// How long a continuous session holds detection after a decode, so the outcome is read before the
    /// camera looks again: about the time to read "Checked in" and lower a badge. A constant rather than
    /// an option, like <see cref="RepeatScanFilter.RepeatWindow"/>: it is a property of handheld
    /// scanning, not of an app.
    /// </summary>
    internal static readonly TimeSpan OutcomeDwell = TimeSpan.FromMilliseconds(1500);

    private static async Task<bool> RequestCameraAsync()
    {
        // ConfigureAwait(true): the platform prompt must be raised from the main thread.
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>().ConfigureAwait(true);
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.Camera>().ConfigureAwait(true);
        }

        return status == PermissionStatus.Granted;
    }

    private static async Task<string?> ScanOnMainThreadAsync(
        string cancelText,
        string cameraDescription,
        CancellationToken cancellationToken)
    {
        var host = CurrentPage;
        if (host is null)
        {
            return null;
        }

        var scanPage = new BarcodeScanPage(cancelText, cameraDescription);

        // ConfigureAwait(true) throughout: modal navigation and the camera view are main-thread
        // bound, and this method is already running on it.
        await host.Navigation.PushModalAsync(scanPage).ConfigureAwait(true);
        try
        {
            await using var registration = cancellationToken.Register(scanPage.Cancel);
            return await scanPage.NextDecodeAsync().ConfigureAwait(true);
        }
        finally
        {
            scanPage.Cancel();
            await host.Navigation.PopModalAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// One continuous session on one modal page: decode, hand the payload to the caller, show the
    /// outcome, hold for <see cref="OutcomeDwell"/>, scan again. Returns the caller's exception, if its
    /// callback threw, so it is rethrown only after the page has been popped.
    /// </summary>
    private static async Task<ExceptionDispatchInfo?> RunSessionOnMainThreadAsync(
        string cancelText,
        string cameraDescription,
        Func<string, CancellationToken, Task<string?>> onScanned,
        CancellationToken cancellationToken)
    {
        var host = CurrentPage;
        if (host is null)
        {
            return null;
        }

        // The callback's token: cancelled with the caller's, and when the operator ends the session,
        // so a lookup still in flight when Cancel is tapped is told to stop.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scanPage = new BarcodeScanPage(cancelText, cameraDescription, session.Cancel);

        await host.Navigation.PushModalAsync(scanPage).ConfigureAwait(true);
        try
        {
            await using var registration = cancellationToken.Register(scanPage.Cancel);
            var repeats = new RepeatScanFilter(TimeProvider.System);
            while (await scanPage.NextDecodeAsync().ConfigureAwait(true) is { } payload)
            {
                if (!repeats.IsRepeat(payload)
                    && await HandleDecodeAsync(scanPage, payload, onScanned, session.Token).ConfigureAwait(true) is { } failure)
                {
                    return failure;
                }

                // Detection stays paused while the operator reads the outcome and lowers the code;
                // a repeat waits too, so a code held in view is not decoded at the camera's frame rate.
                await Task.WhenAny(Task.Delay(OutcomeDwell, CancellationToken.None), scanPage.Ended).ConfigureAwait(true);
            }

            return null;
        }
        finally
        {
            // Ends the session (and cancels the callback token) before the token source is disposed,
            // so the page's later OnDisappearing finds it already ended. The single pop, as for a scan.
            scanPage.Cancel();
            await host.Navigation.PopModalAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Hands one new payload to the caller and shows what it returns. The previous outcome is cleared
    /// first, so the line always belongs to the code just scanned.
    /// </summary>
    /// <returns>The callback's exception, captured, or <see langword="null"/>.</returns>
    private static async Task<ExceptionDispatchInfo?> HandleDecodeAsync(
        BarcodeScanPage scanPage,
        string payload,
        Func<string, CancellationToken, Task<string?>> onScanned,
        CancellationToken sessionToken)
    {
        scanPage.ShowOutcome(null);

        string? outcome;
        try
        {
            outcome = await onScanned(payload, sessionToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
            // The session ended while the callback ran: nothing left to show.
            return null;
        }
#pragma warning disable CA1031 // Do not catch general exception types - the caller's exception is captured here and rethrown once the page is popped
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ExceptionDispatchInfo.Capture(ex);
        }

        if (!scanPage.HasEnded)
        {
            scanPage.ShowOutcome(outcome);
        }

        return null;
    }

    private static Page? CurrentPage
    {
        get
        {
            // Application.MainPage is obsolete on the .NET 10 MAUI train; the window's Page is the
            // supported way to reach the active navigation stack.
            var windows = Application.Current?.Windows;
            return windows is { Count: > 0 } ? windows[0].Page : null;
        }
    }
}
