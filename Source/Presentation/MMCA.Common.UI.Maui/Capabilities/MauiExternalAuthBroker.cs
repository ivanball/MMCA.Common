using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IExternalAuthBroker"/> (ADR-043/044): runs the provider flow in the system
/// browser via <c>WebAuthenticator</c> (providers reject embedded WebViews), captures the
/// single-use completion code from the app's custom-scheme callback, and hands it to the
/// shared <c>/auth/oauth-complete</c> page, which already owns the exchange, token storage,
/// and auth-state refresh. Requires <c>OAuth:MobileRedirectScheme</c> in the head's
/// configuration (also allow-listed server-side via <c>OAuth:AllowedReturnUrlSchemes</c>)
/// plus the platform callback registrations; unset means unavailable and the Login page
/// keeps its web anchor flow.
/// </summary>
public sealed class MauiExternalAuthBroker : IExternalAuthBroker
{
    private readonly NavigationManager _navigationManager;
    private readonly IOptions<ApiSettings> _apiSettings;
    private readonly string? _callbackScheme;

    /// <summary>Initializes the broker from the head's configuration.</summary>
    public MauiExternalAuthBroker(
        NavigationManager navigationManager,
        IOptions<ApiSettings> apiSettings,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _navigationManager = navigationManager;
        _apiSettings = apiSettings;
        _callbackScheme = configuration["OAuth:MobileRedirectScheme"];
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_callbackScheme);

    /// <inheritdoc />
    public async Task<bool> SignInAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (!IsAvailable)
        {
            return false;
        }

        var apiBase = _apiSettings.Value.ApiEndpoint?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            return false;
        }

        var callbackUrl = new Uri($"{_callbackScheme}://oauth-complete");
        var authorizeUrl = new Uri(
            $"{apiBase}/auth/oauth/{Uri.EscapeDataString(provider)}?returnUrl={Uri.EscapeDataString(callbackUrl.OriginalString)}");

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions
                {
                    Url = authorizeUrl,
                    CallbackUrl = callbackUrl,
                },
                cancellationToken).ConfigureAwait(false);

            if (result?.Properties is null
                || !result.Properties.TryGetValue("code", out var code)
                || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            // The shared completion page owns the exchange + token storage + auth-state refresh;
            // reusing it keeps the single-use-code contract in exactly one place.
            _navigationManager.NavigateTo($"/auth/oauth-complete?code={Uri.EscapeDataString(code)}");
            return true;
        }
        catch (TaskCanceledException)
        {
            // User dismissed the system browser window.
            return false;
        }
        catch (FeatureNotSupportedException)
        {
            return false;
        }
    }
}
