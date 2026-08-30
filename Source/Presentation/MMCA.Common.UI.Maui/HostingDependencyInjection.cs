using MMCA.Common.UI.Maui.Capabilities;
using MMCA.Common.UI.Maui.Globalization;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Capabilities;
using Plugin.LocalNotification;
using ZXing.Net.Maui.Controls;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// <see cref="MauiAppBuilder"/>-level entry point for the device-capability layer (ADR-042), the
/// hybrid culture wiring (ADR-027), and the process-wide unhandled-exception hook. Call AFTER
/// <c>AddUIShared</c> in <c>MauiProgram.CreateMauiApp</c>.
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
        /// Installs the two process-wide last-chance exception handlers,
        /// <see cref="AppDomain.UnhandledException"/> and
        /// <see cref="TaskScheduler.UnobservedTaskException"/>, so a throw on a background thread
        /// or a faulted fire-and-forget task is written to the app's logger (category
        /// <c>MMCA.Common.UI.Maui.UnhandledException</c>, level Critical) instead of vanishing. The
        /// unobserved-task handler marks the exception observed, which is what stops the finalizer
        /// thread from escalating a task nobody awaited into a process kill at the next collection.
        /// <para>
        /// <b>What it cannot catch.</b> Anything that never becomes a CLR exception: a native
        /// crash (SIGSEGV, an Objective-C NSException on iOS, an Android ANR), a stack overflow, or
        /// a fail-fast. Those tear the process down below the runtime and no managed handler runs,
        /// so a head that needs full crash coverage still pairs this with a platform crash
        /// reporter. Handler bodies swallow their own failures on purpose: a reporter that throws
        /// on the last-chance path replaces one crash with a worse one.
        /// </para>
        /// <para>
        /// Call it ONCE, in <c>MauiProgram.CreateMauiApp</c>. The handlers are process-wide, so a
        /// second call is ignored rather than doubling every report. Unlike the other calls here,
        /// ordering does not matter: the handlers are installed when the app is built (through
        /// <see cref="MauiErrorHandlingInitializer"/>), so a logging provider registered after this
        /// call is still picked up.
        /// </para>
        /// </summary>
        /// <param name="onUnhandled">
        /// Optional crash-reporter hook, invoked with the exception and a source tag
        /// (<see cref="MauiErrorHandlingInitializer.AppDomainSource"/> or
        /// <see cref="MauiErrorHandlingInitializer.TaskSchedulerSource"/>). It runs inside the
        /// handler's own guard, so a throw from it cannot make the crash worse; keep it fast and
        /// non-blocking, because the AppDomain path is usually milliseconds from process death.
        /// </param>
        public MauiAppBuilder UseMmcaMauiErrorHandling(Action<Exception, string>? onUnhandled = null)
        {
            builder.Services.AddSingleton<IMauiInitializeService>(new MauiErrorHandlingInitializer(onUnhandled));
            return builder;
        }

        /// <summary>
        /// Opt-in camera barcode/QR scanning (ADR-042) with the scan page's text resolved lazily:
        /// registers the ZXing.Net.MAUI handlers (<c>UseBarcodeReader()</c>) and overrides the null
        /// <c>IBarcodeScannerService</c> with <see cref="MauiBarcodeScannerService"/>. Deliberately
        /// NOT folded into <see cref="UseMauiDeviceCapabilities"/>: a head that never scans should
        /// ship neither the camera handler nor a camera permission declaration.
        /// <para>
        /// Both delegates are invoked once per scan, when the page is built, so the modal follows the
        /// user's in-app language choice rather than the device language that was active at startup
        /// (everything here runs while the app is being built, which is BEFORE
        /// <see cref="Globalization.MauiCultureInitializer"/> restores the persisted language,
        /// ADR-027). Pass the resource lookups themselves, for example
        /// <c>() =&gt; localizer["Cancel"]</c>, and keep them cheap and side-effect free.
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
