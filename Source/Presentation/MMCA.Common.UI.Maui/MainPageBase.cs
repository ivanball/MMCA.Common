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

    /// <summary>
    /// Whether the Android status bar draws dark icons (<see langword="true"/>, for a light app bar),
    /// light icons (<see langword="false"/>, for a dark one), or is left as the platform theme sets it
    /// (<see langword="null"/>, the default). Read when the page is attached to its window and again
    /// whenever the app theme changes (<c>NativeThemeSync</c> sets it on every Day/Dark toggle), so an
    /// override may answer from <see cref="Application.RequestedTheme"/> when the head's chrome changes
    /// colour with the theme. Without an override, a light app bar (Store's pink chrome) kept the white
    /// icons of the dark platform default. The other platforms ignore it.
    /// </summary>
    protected virtual bool? UseDarkStatusBarIcons => null;

#if ANDROID
    private bool _statusBarHookAttached;
    private bool _statusBarAppearanceHooked;

    /// <inheritdoc />
    /// <remarks>
    /// The page's handler, and with it the activity window, exists from here, so the status bar icon
    /// appearance is applied now and re-applied on every app theme change.
    /// </remarks>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null)
        {
            return;
        }

        ApplyStatusBarIconAppearance();
        if (!_statusBarAppearanceHooked && Application.Current is { } app)
        {
            _statusBarAppearanceHooked = true;
            app.RequestedThemeChanged += OnRequestedThemeChanged;
        }
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ApplyStatusBarIconAppearance);

    /// <summary>
    /// Applies <see cref="UseDarkStatusBarIcons"/> through the window's insets controller
    /// (<c>AppearanceLightStatusBars</c>: a light status bar is one drawn with dark icons). Does
    /// nothing when the override answers <see langword="null"/> or no activity window exists yet.
    /// </summary>
    private void ApplyStatusBarIconAppearance()
    {
        if (UseDarkStatusBarIcons is not { } darkIcons)
        {
            return;
        }

        var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
        if (window?.DecorView is not { } decorView)
        {
            return;
        }

        if (AndroidX.Core.View.WindowCompat.GetInsetsController(window, decorView) is { } insetsController)
        {
            insetsController.AppearanceLightStatusBars = darkIcons;
        }
    }

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
    /// report the inset reliably, so only Android sends it. The navigation bar takes the same route
    /// (<c>MmcaNavigationBar/&lt;css px&gt;</c>, <c>--mmca-navigation-bar-fallback</c>): the system
    /// resolves its height for the active mode, three-button or gesture.
    /// </summary>
    private static void OnHostWebViewInitialized(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializedEventArgs e)
    {
        var webView = e.WebView;
        var resources = webView.Resources;
        if (resources is null)
        {
            return;
        }

        var tokens = string.Concat(
            SystemBarToken(resources, "status_bar_height", "MmcaStatusBar"),
            SystemBarToken(resources, "navigation_bar_height", "MmcaNavigationBar"),
            ThemeToken());
        if (tokens.Length > 0)
        {
            webView.Settings.UserAgentString = webView.Settings.UserAgentString + tokens;
        }
    }

    /// <summary>The user agent token for one system bar, or empty when the platform has no such dimension.</summary>
    private static string SystemBarToken(Android.Content.Res.Resources resources, string dimensionName, string tokenName)
    {
        var dimensionId = resources.GetIdentifier(dimensionName, "dimen", "android");
        if (dimensionId == 0)
        {
            return string.Empty;
        }

        var density = resources.DisplayMetrics?.Density ?? 1f;
        var cssPixels = (int)Math.Ceiling(resources.GetDimensionPixelSize(dimensionId) / (density > 0 ? density : 1f));
        return cssPixels > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" {tokenName}/{cssPixels}")
            : string.Empty;
    }
#endif

#if IOS || MACCATALYST
    private bool _themeHookAttached;

    /// <inheritdoc />
    /// <remarks>
    /// Runs before the web view handler exists, so the hook below sees the web view's configuration
    /// while it can still change: WKWebView fixes its user agent when it is created.
    /// </remarks>
    protected override void OnHandlerChanging(HandlerChangingEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        base.OnHandlerChanging(args);
        if (args.NewHandler is not null && !_themeHookAttached)
        {
            _themeHookAttached = true;
            HostWebView.BlazorWebViewInitializing += OnHostWebViewInitializing;
        }
    }

    /// <summary>
    /// Hands the natively saved Day/Dark mode to the page as the same <c>MmcaTheme/dark|light</c> user
    /// agent token Android sends, appended to WKWebView's application name (the documented way to extend
    /// its default user agent). Without it an iOS relaunch right after a theme switch painted the mode
    /// the WebView had last flushed to its own storage (Store Run 4 X-30).
    /// </summary>
    private static void OnHostWebViewInitializing(object? sender, Microsoft.AspNetCore.Components.WebView.BlazorWebViewInitializingEventArgs e)
    {
        var token = ThemeToken();
        if (token.Length > 0 && e.Configuration is { } configuration)
        {
            configuration.ApplicationNameForUserAgent = (configuration.ApplicationNameForUserAgent ?? string.Empty) + token;
        }
    }
#endif

#if ANDROID || IOS || MACCATALYST
    /// <summary>
    /// The user agent token for the Day/Dark mode saved natively (<c>MmcaTheme/dark|light</c>), or empty
    /// on a first launch. <c>NativeThemeSync</c> saves it on every change, while the WebView flushes its
    /// own storage lazily, so after a quick kill this is the value saved last: <c>theme-prepaint.js</c>
    /// paints the relaunch's first frame from it (Run 17 X-27, Store Run 4 X-30).
    /// </summary>
    private static string ThemeToken() => MauiThemeStore.ReadStoredIsDarkMode() switch
    {
        true => " MmcaTheme/dark",
        false => " MmcaTheme/light",
        null => string.Empty,
    };
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
