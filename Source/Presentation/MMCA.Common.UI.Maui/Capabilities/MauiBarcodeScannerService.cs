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
/// </summary>
/// <param name="cancelText">Label for the scan page's cancel button; pass a localized string.</param>
/// <param name="cameraDescription">Accessible description and title for the scan surface.</param>
public sealed class MauiBarcodeScannerService(string cancelText, string cameraDescription) : IBarcodeScannerService
{
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
            return await MainThread
                .InvokeOnMainThreadAsync(() => ScanOnMainThreadAsync(cancelText, cameraDescription, cancellationToken))
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
