using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.JSInterop;
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
    /// The <c>BlazorWebView</c> declared in the derived page's XAML. Implemented as an expression
    /// body over the generated <c>x:Name</c> field (e.g. <c>=&gt; blazorWebView;</c>): the field is
    /// private to the derived partial class, so the base can only reach it through this override.
    /// </summary>
    protected abstract BlazorWebView HostWebView { get; }

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
