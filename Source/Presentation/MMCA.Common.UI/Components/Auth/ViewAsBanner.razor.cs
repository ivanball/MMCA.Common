using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// The read-only view-as banner (ADR-137): "Viewing as {name}, read-only, ends in mm:ss" with an End
/// button, shown only while the principal carries <c>act_sub</c>. <see cref="Layout.MainLayout"/>
/// places it above every page.
/// <para>
/// <b>Web only.</b> End posts to the same-origin proxy's <c>{ApiPathPrefix}/auth/view-as/end</c>
/// from the browser (so the proxy can delete its overlay cookie) and then reloads the app on the
/// staff user's own session. When the countdown reaches zero the app reloads the same way.
/// </para>
/// </summary>
public partial class ViewAsBanner : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private bool _viewAs;
    private bool _ending;
    private string _viewedName = string.Empty;
    private DateTimeOffset? _expiresAt;
    private PeriodicTimer? _timer;

    /// <summary>The same-origin proxy's path prefix (default <c>/api</c>).</summary>
    [Parameter]
    public string? ApiPathPrefix { get; set; }

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>
    /// The time left on a view-as token, given its <c>exp</c> claim, or <see langword="null"/> when
    /// the principal carries no readable expiry. Never negative.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The remaining time, or <see langword="null"/>.</returns>
    public static TimeSpan? Remaining(ClaimsPrincipal? user, DateTimeOffset now)
    {
        var exp = user?.FindFirst("exp")?.Value;
        if (!long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var left = DateTimeOffset.FromUnixTimeSeconds(seconds) - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _timer?.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        var user = AuthenticationState is null ? null : (await AuthenticationState).User;
        _viewAs = user.IsViewAs();
        _viewedName = user?.Identity?.Name ?? user?.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
        _expiresAt = Remaining(user, DateTimeOffset.UtcNow) is { } left ? DateTimeOffset.UtcNow + left : null;
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        // The countdown needs an interactive renderer; a prerender shows the starting value only.
        if (firstRender && _viewAs && _timer is null && RendererInfo.IsInteractive)
        {
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            _ = TickAsync(_timer);
        }
    }

    private string FormatRemaining()
    {
        var left = _expiresAt is { } expires ? expires - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (left < TimeSpan.Zero)
        {
            left = TimeSpan.Zero;
        }

        return left.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private async Task TickAsync(PeriodicTimer timer)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.LifetimeToken()))
            {
                await InvokeAsync(StateHasChanged);
                if (_expiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
                {
                    await InvokeAsync(EndAsync);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (ObjectDisposedException)
        {
            // Disposed between ticks.
        }
    }

    private async Task EndAsync()
    {
        if (_ending)
        {
            return;
        }

        _ending = true;
        try
        {
            await JS.InvokeVoidAsync("mmcaViewAs.end", _cts.LifetimeToken(), (ApiPathPrefix ?? "/api").TrimEnd('/') + "/auth/view-as/end");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            // The page is reloading or the circuit is gone; the overlay expires on its own.
            _ending = false;
        }
    }
}
