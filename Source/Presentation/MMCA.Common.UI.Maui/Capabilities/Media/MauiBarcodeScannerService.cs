using MMCA.Common.UI.Services.Capabilities.Media;

namespace MMCA.Common.UI.Maui.Capabilities.Media;

/// <summary>
/// MAUI camera scanner over ZXing.Net.MAUI (ADR-042). Pushes <see cref="BarcodeScanPage"/> modally
/// over the current window page, resolves on the first decoded payload, and pops the page again on
/// every exit path. The head declares the camera permission (Android CAMERA, iOS
/// NSCameraUsageDescription) and this service requests it before the page opens, because the camera
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
        catch
        {
            // A head that never declared the permission throws here, and that must read as "no camera access"
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
        catch
        {
            // Scanning is best-effort; a missing window, a denied camera, and a handler-less platform must all read as "no scan"
            return null;
        }
    }

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
            return await scanPage.Completion.ConfigureAwait(true);
        }
        finally
        {
            await host.Navigation.PopModalAsync().ConfigureAwait(true);
        }
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
