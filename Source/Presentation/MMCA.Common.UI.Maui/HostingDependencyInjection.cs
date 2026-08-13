using MMCA.Common.UI.Maui.Capabilities;
using MMCA.Common.UI.Maui.Globalization;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Capabilities;
using Plugin.LocalNotification;
using ZXing.Net.Maui.Controls;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// <see cref="MauiAppBuilder"/>-level entry point for the device-capability layer (ADR-042) and the
/// hybrid culture wiring (ADR-027). Call AFTER <c>AddUIShared</c> in <c>MauiProgram.CreateMauiApp</c>.
/// </summary>
public static class HostingDependencyInjection
{
    extension(MauiAppBuilder builder)
    {
        /// <summary>
        /// Registers the native capability implementations plus the platform hooks that need
        /// the builder: Plugin.LocalNotification lifecycle wiring and the notification-tap
        /// deep-link bridge (<see cref="DeviceCapabilitiesInitializer"/>).
        /// <para>
        /// Heads must ALSO chain <c>.UseMauiCommunityToolkit()</c> onto their own
        /// <c>UseMauiApp&lt;T&gt;()</c> call (speech-to-text depends on it): the toolkit's
        /// MCT001 analyzer requires the call to appear in the app's builder chain, so this
        /// wrapper cannot make it for you.
        /// </para>
        /// </summary>
        public MauiAppBuilder UseMauiDeviceCapabilities()
        {
            builder.UseLocalNotification();
            builder.Services.AddMauiDeviceCapabilities();
            builder.Services.AddSingleton<IMauiInitializeService, DeviceCapabilitiesInitializer>();

            // Folded in deliberately, despite belonging to ADR-027 rather than ADR-042: a hybrid head
            // that skips it gets a culture switcher that navigates to a server endpoint it does not
            // host, which renders the not-found page. Every head already makes this call, so wiring it
            // here means no head can be left half-configured. UseMauiCulture() stays separately
            // callable for a head that composes its registrations by hand.
            builder.UseMauiCulture();
            return builder;
        }

        /// <summary>
        /// Opt-in camera barcode/QR scanning (ADR-042): registers the ZXing.Net.MAUI handlers
        /// (<c>UseBarcodeReader()</c>) and overrides the null <c>IBarcodeScannerService</c> with
        /// <see cref="MauiBarcodeScannerService"/>. Deliberately NOT folded into
        /// <see cref="UseMauiDeviceCapabilities"/>: a head that never scans should ship neither the
        /// camera handler nor a camera permission declaration.
        /// <para>
        /// The head still declares the platform permission itself (Android CAMERA, iOS
        /// NSCameraUsageDescription). Call AFTER <c>AddUIShared</c> so the plain Add overrides the
        /// TryAdd default (last registration wins).
        /// </para>
        /// <para>
        /// This overload fixes both strings at startup. Everything here runs while the app is being
        /// built, which is BEFORE <see cref="Globalization.MauiCultureInitializer"/> restores the
        /// user's persisted language (ADR-027), so the values are resolved under the DEVICE language
        /// and never change afterwards, not even when the user switches language in the app. On any
        /// head with a language switcher, call the
        /// <c>UseCommonBarcodeScanner(Func&lt;string&gt;, Func&lt;string&gt;)</c> overload below instead
        /// (a cref cannot name a sibling extension member here).
        /// </para>
        /// </summary>
        /// <param name="cancelText">Label for the scan page's cancel button; fixed at startup.</param>
        /// <param name="cameraDescription">Accessible description and title for the scan surface; fixed at startup.</param>
        public MauiAppBuilder UseCommonBarcodeScanner(
            string cancelText = "Cancel",
            string cameraDescription = "Scan a code") =>
            builder.UseCommonBarcodeScanner(() => cancelText, () => cameraDescription);

        /// <summary>
        /// Opt-in camera barcode/QR scanning (ADR-042) with the scan page's text resolved lazily:
        /// registers the ZXing.Net.MAUI handlers (<c>UseBarcodeReader()</c>) and overrides the null
        /// <c>IBarcodeScannerService</c> with <see cref="MauiBarcodeScannerService"/>. Deliberately
        /// NOT folded into <see cref="UseMauiDeviceCapabilities"/>: a head that never scans should
        /// ship neither the camera handler nor a camera permission declaration.
        /// <para>
        /// This is the localization-correct overload. Both delegates are invoked once per scan, when
        /// the page is built, so the modal follows the user's in-app language choice instead of the
        /// device language that was active at startup. Pass the resource lookups themselves, for
        /// example <c>() =&gt; localizer["Cancel"]</c>, and keep them cheap and side-effect free.
        /// </para>
        /// <para>
        /// The head still declares the platform permission itself (Android CAMERA, iOS
        /// NSCameraUsageDescription). Call AFTER <c>AddUIShared</c> so the plain Add overrides the
        /// TryAdd default (last registration wins).
        /// </para>
        /// </summary>
        /// <param name="cancelText">Resolves the label for the scan page's cancel button, per scan.</param>
        /// <param name="cameraDescription">Resolves the accessible description and title for the scan surface, per scan.</param>
        public MauiAppBuilder UseCommonBarcodeScanner(
            Func<string> cancelText,
            Func<string> cameraDescription)
        {
            ArgumentNullException.ThrowIfNull(cancelText);
            ArgumentNullException.ThrowIfNull(cameraDescription);

            builder.UseBarcodeReader();
            builder.Services.AddSingleton<IBarcodeScannerService>(
                _ => new MauiBarcodeScannerService(cancelText, cameraDescription));
            return builder;
        }

        /// <summary>
        /// Wires culture switching for a hybrid head (ADR-027): replaces the web
        /// <c>ICultureApplier</c> (which round-trips the server <c>/culture/set</c> endpoint that no
        /// hybrid head hosts) with the in-process <see cref="MauiCultureApplier"/>, and restores the
        /// persisted culture at startup via <see cref="MauiCultureInitializer"/>.
        /// <para>
        /// Already called by <see cref="UseMauiDeviceCapabilities"/>; calling it twice is harmless.
        /// Must run AFTER <c>AddUIShared</c> so the plain Add overrides that TryAdd default
        /// (last registration wins).
        /// </para>
        /// </summary>
        public MauiAppBuilder UseMauiCulture()
        {
            builder.Services.AddScoped<ICultureApplier, MauiCultureApplier>();
            builder.Services.AddSingleton<IMauiInitializeService, MauiCultureInitializer>();
            return builder;
        }
    }
}
