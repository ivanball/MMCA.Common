using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI camera scanner over ZXing.Net.MAUI (ADR-042). Pushes <see cref="BarcodeScanPage"/> modally
/// over the current window page, resolves on the first decoded payload, and pops the page again on
/// every exit path. The camera permission belongs to the platform (Android CAMERA, iOS
/// NSCameraUsageDescription): a head that has not declared it, or a user who denies it, gets a scan
/// that simply never decodes and is cancelled out of, which the contract surfaces as
/// <see langword="null"/> rather than an exception.
/// <para>
/// Registered by <c>UseCommonBarcodeScanner()</c> only, so a head that never scans neither ships
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
    /// Initializes the service with text resolved lazily, per scan. This is the localization-correct
    /// overload: pass the resource lookups themselves (for example
    /// <c>() =&gt; Localizer["Cancel"]</c>) so each scan page is built under the
    /// <see cref="System.Globalization.CultureInfo.CurrentUICulture"/> in effect at that moment.
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

    /// <summary>
    /// Initializes the service with fixed text. The values are captured as given and never
    /// re-resolved, so they stay in whatever language was active when the service was constructed
    /// (the device language, on a head that persists a different in-app choice). Prefer the
    /// <see cref="MauiBarcodeScannerService(Func{string}, Func{string})"/> overload on any head that
    /// offers a language switcher.
    /// </summary>
    /// <param name="cancelText">Label for the scan page's cancel button.</param>
    /// <param name="cameraDescription">Accessible description and title for the scan surface.</param>
    public MauiBarcodeScannerService(string cancelText, string cameraDescription)
        : this(() => cancelText, () => cameraDescription)
    {
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
    public async Task<string?> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || cancellationToken.IsCancellationRequested)
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
