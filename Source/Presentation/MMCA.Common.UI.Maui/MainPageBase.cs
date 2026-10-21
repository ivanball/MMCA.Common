using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.JSInterop;
using MMCA.Common.UI.Maui.Theme;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Maui;

/// <summary>
/// Base content page for a MAUI Blazor Hybrid head whose XAML hosts a single <c>BlazorWebView</c>.
/// Intercepts the platform back-button gesture (Android hardware back, iOS swipe) and forwards it
/// to the WebView's internal history stack via <see cref="MauiBackNavigationBridge"/>, exiting the
/// app only when the WebView has nowhere to go back to.
/// <para>
/// The base owns no XAML of its own, so a head adopts it in two edits: point the XAML root element
/// at this type (<c>&lt;maui:MainPageBase ... x:Class="MyApp.UI.MainPage"&gt;</c> with
/// <c>xmlns:maui="clr-namespace:MMCA.Common.UI.Maui;assembly=MMCA.Common.UI.Maui"</c>), and reduce
/// the code-behind to the partial class, <c>InitializeComponent()</c>, and a
/// <see cref="HostWebView"/> override returning the <c>x:Name</c>d control.
/// </para>
/// </summary>
public abstract class MainPageBase : ContentPage
{
    /// <summary>
    /// Initializes the page. Applies the persisted Day/Dark mode (ADR-028) to the native app theme
    /// first, so the page behind the <c>BlazorWebView</c> starts in that theme instead of flashing the
    /// OS theme until Blazor's first render (<c>NativeThemeSync</c> keeps it in sync afterwards).
    /// </summary>
    protected MainPageBase() => MauiThemeStore.ApplyStoredTheme();

    /// <summary>
    /// The <c>BlazorWebView</c> declared in the derived page's XAML. Implemented as an expression
    /// body over the generated <c>x:Name</c> field (e.g. <c>=&gt; blazorWebView;</c>): the field is
    /// private to the derived partial class, so the base can only reach it through this override.
    /// </summary>
    protected abstract BlazorWebView HostWebView { get; }

#if ANDROID
    private bool _statusBarHookAttached;

    /// <inheritdoc />
    /// <remarks>
    /// Runs before the page's handler (and with it the native web view) is created, which is the
    /// first moment <see cref="HostWebView"/> exists (the derived constructor has run) and the last
    /// one before the web view is initialized, so the status bar token below reaches the first load.
    /// </remarks>
    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        base.OnHandlerChanging(args);
        if (args.NewHandler is not null && !_statusBarHookAttached)
        {
            _statusBarHookAttached = true;
            HostWebView.BlazorWebViewInitialized += OnHostWebViewInitialized;
        }
    }

    /// <summary>
    /// Hands the status bar height to the page as a user agent token (<c>MmcaStatusBar/&lt;css px&gt;</c>).
    /// A head drawn edge to edge pads <c>index.html</c> with <c>env(safe-area-inset-top)</c>, which the
    /// Android web view reports as 0 on a cold launch (the window insets reach it only after the first
    /// layout), so the app bar drew under the status bar. The height is a platform dimension known
    /// natively before the page loads; <c>theme-prepaint.js</c> (MMCA.Common.UI) turns the token into
    /// <c>--mmca-status-bar-fallback</c>, and the head sizes its spacer with
    /// <c>max(env(safe-area-inset-top), var(--mmca-status-bar-fallback, 0px))</c>. The other platforms
    /// report the inset reliably, so only Android sends it.
    /// </summary>
    private static void OnHostWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
        var webView = e.WebView;
        var resources = webView.Resources;
        if (resources is null)
        {
            return;
        }

        var dimensionId = resources.GetIdentifier("status_bar_height", "dimen", "android");
        if (dimensionId == 0)
        {
            return;
        }

        var density = resources.DisplayMetrics?.Density ?? 1f;
        var cssPixels = (int)Math.Ceiling(resources.GetDimensionPixelSize(dimensionId) / (density > 0 ? density : 1f));
        if (cssPixels <= 0)
        {
            return;
        }

        webView.Settings.UserAgentString = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{webView.Settings.UserAgentString} MmcaStatusBar/{cssPixels}");
    }
#endif

    /// <inheritdoc />
    protected override bool OnBackButtonPressed()
    {
        // Consume the gesture and process the back navigation off the UI thread.
        _ = HandleBackAsync();
        return true;
    }

    private static void CaptureJsRuntime(IServiceProvider sp, TaskCompletionSource<IJSRuntime?> tcs) =>
        tcs.TrySetResult(sp.GetService<IJSRuntime>());

    private static void QuitApp() =>
        MainThread.BeginInvokeOnMainThread(QuitOnMainThread);

    private static void QuitOnMainThread() =>
        Application.Current?.Quit();

    private async Task HandleBackAsync()
    {
        try
        {
            // BlazorWebView only exposes the synchronous Action<IServiceProvider> dispatch
            // overload, so capture the renderer-scoped IJSRuntime through a TaskCompletionSource
            // and run the async interop work outside the dispatch context.
            var tcs = new TaskCompletionSource<IJSRuntime?>();
            var dispatched = await HostWebView.TryDispatchAsync(sp => CaptureJsRuntime(sp, tcs));

            if (!dispatched)
            {
                QuitApp();
                return;
            }

            var jsRuntime = await tcs.Task;
            if (jsRuntime is null)
            {
                QuitApp();
                return;
            }

            var result = await MauiBackNavigationBridge.HandleBackPressedAsync(jsRuntime);
            if (result.AtRoot)
            {
                QuitApp();
            }
        }
#pragma warning disable CA1031 // Do not catch general exception types - the interop failure modes differ per platform and none of them are recoverable here
        catch
#pragma warning restore CA1031
        {
            // BlazorWebView not yet hydrated or interop failed: exit cleanly.
            QuitApp();
        }
    }
}
