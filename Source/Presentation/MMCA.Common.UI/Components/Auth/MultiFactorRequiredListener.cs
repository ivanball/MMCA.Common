using Microsoft.AspNetCore.Components;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Auth.TwoFactor;
using MMCA.Common.UI.Services.Navigation;
using MMCA.Common.UI.TwoFactor;

namespace MMCA.Common.UI.Components.Auth;

/// <summary>
/// Sends the user to the two-factor page when the API refuses this session for want of a second
/// factor (<c>Authorization.MultiFactorRequired</c>): an account whose role requires one and has not
/// enrolled sets it up there, and an enrolled account whose session started without its code is told
/// to sign in again with it. The current page is kept as the return address.
/// </summary>
/// <remarks>
/// Subscribes to <see cref="MultiFactorRequiredSignal"/> under this scope's token storage, the same
/// instance the page's API services attach their bearer from, so a refusal raised from the API
/// client's handler scope reaches this circuit. Nothing happens on the two-factor page itself or on
/// the sign-in page, and nothing during a static prerender: the interactive render repeats the call
/// and is redirected then. Renders nothing. Reaches the layout only through
/// <see cref="TwoFactorUIModule.LayoutComponentTypes"/>, so a host that never called
/// <c>AddTwoFactorUI()</c> has no listener and its API client never reads a 403 body.
/// </remarks>
internal sealed class MultiFactorRequiredListener : ComponentBase, IDisposable
{
    private IDisposable? _subscription;

    [Inject] private ITokenStorageService TokenStorage { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    /// <inheritdoc />
    protected override void OnInitialized() =>
        _subscription = MultiFactorRequiredSignal.Subscribe(TokenStorage, OnRequired);

    /// <summary>Whether <paramref name="relativePath"/> is a page the redirect must never leave.</summary>
    /// <param name="relativePath">The base-relative path of the current location.</param>
    /// <returns><see langword="true"/> for the two-factor page and the sign-in page.</returns>
    private static bool IsExempt(string relativePath) =>
        relativePath.StartsWith(TwoFactorUIModule.AccountPath.TrimStart('/'), StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith("login", StringComparison.OrdinalIgnoreCase);

    private void OnRequired() => _ = RedirectAsync();

    private async Task RedirectAsync()
    {
        try
        {
            await InvokeAsync(Redirect);
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended between the refusal and the dispatch.
        }
        catch (InvalidOperationException)
        {
            // Same race, surfaced by the renderer as an unavailable dispatcher.
        }
    }

    private void Redirect()
    {
        if (!RendererInfo.IsInteractive)
        {
            return;
        }

        var relative = Navigation.ToBaseRelativePath(Navigation.Uri);
        if (IsExempt(relative))
        {
            return;
        }

        var returnUrl = ReturnUrlProtector.Sanitize("/" + relative, "/");
        Navigation.NavigateTo(
            $"{TwoFactorUIModule.AccountPath}?required=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }
}
