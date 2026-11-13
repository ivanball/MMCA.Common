using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth.OAuth;
using MMCA.Common.UI.Services.Capabilities.Auth;

namespace MMCA.Common.UI.Maui.Capabilities.Auth;

/// <summary>
/// MAUI <see cref="IExternalAuthBroker"/> (ADR-043): runs the provider flow in the system
/// browser via <c>WebAuthenticator</c> (providers reject embedded WebViews), captures the
/// single-use completion code from the app's custom-scheme callback, and hands it to the
/// shared <c>/auth/oauth-complete</c> page, which already owns the exchange, token storage,
/// and auth-state refresh. Requires <c>OAuth:MobileRedirectScheme</c> in the head's
/// configuration (also allow-listed server-side via <c>OAuth:AllowedReturnUrlSchemes</c>)
/// plus the platform callback registrations; unset means unavailable and the Login page
/// keeps its web anchor flow.
/// <para>
/// Every attempt logs one start line and the exit it took, with the elapsed time, because the page
/// shows the same message for all of them. The lines carry fixed exit names and the server's error
/// name only: the callback's code and state, and any exception text (a redirect failure quotes the
/// callback URI), stay out.
/// </para>
/// </summary>
public sealed partial class MauiExternalAuthBroker : IExternalAuthBroker
{
    private readonly NavigationManager _navigationManager;
    private readonly IOptions<ApiSettings> _apiSettings;
    private readonly OAuthFlowStateStore _flowState;
    private readonly ILogger<MauiExternalAuthBroker> _logger;
    private readonly string? _callbackScheme;

    /// <summary>Initializes the broker from the head's configuration.</summary>
    public MauiExternalAuthBroker(
        NavigationManager navigationManager,
        IOptions<ApiSettings> apiSettings,
        OAuthFlowStateStore flowState,
        IConfiguration configuration,
        ILogger<MauiExternalAuthBroker> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _navigationManager = navigationManager;
        _apiSettings = apiSettings;
        _flowState = flowState;
        _logger = logger;
        _callbackScheme = configuration["OAuth:MobileRedirectScheme"];
    }

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_callbackScheme);

    /// <inheritdoc />
    public async Task<bool> SignInAsync(string provider, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var started = Stopwatch.GetTimestamp();
        LogStarted(_logger, provider);

        if (!IsAvailable)
        {
            return Exit(provider, "NoCallbackScheme", started, succeeded: false);
        }

        var apiBase = _apiSettings.Value.ApiEndpoint?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            return Exit(provider, "NoApiEndpoint", started, succeeded: false);
        }

        var callbackUrl = new Uri($"{_callbackScheme}://oauth-complete");

        // SECURITY: the completion page refuses a code that does not belong to an attempt started
        // on this device (a deep link carrying someone else's code would otherwise sign the app in
        // as them), so the brokered flow starts one too and round-trips its value.
        var flowState = await _flowState.BeginAsync(cancellationToken).ConfigureAwait(false);
        var stateQuery = string.IsNullOrEmpty(flowState)
            ? string.Empty
            : $"&state={Uri.EscapeDataString(flowState)}";
        var authorizeUrl = new Uri(
            $"{apiBase}/auth/oauth/{Uri.EscapeDataString(provider)}?returnUrl={Uri.EscapeDataString(callbackUrl.OriginalString)}{stateQuery}");

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new WebAuthenticatorOptions
                {
                    Url = authorizeUrl,
                    CallbackUrl = callbackUrl,
                },
                cancellationToken).ConfigureAwait(false);

            return CompleteSignIn(provider, result, started);
        }
        catch (TaskCanceledException)
        {
            // The user dismissed the system browser, a newer attempt replaced this one, or the
            // platform's launch activity resumed with no callback. The elapsed time tells a sheet
            // the user closed from one that never appeared.
            var exit = cancellationToken.IsCancellationRequested ? "CancelledByCaller" : "CancelledWithoutCallback";
            return Exit(provider, exit, started, succeeded: false);
        }
        catch (FeatureNotSupportedException)
        {
            return Exit(provider, "NotSupported", started, succeeded: false);
        }
        catch (Exception ex) when (LogUnexpectedExit(provider, ex, started))
        {
            throw; // Unreachable: the filter only records the exit and always declines.
        }
    }

    private bool CompleteSignIn(string provider, WebAuthenticatorResult? result, long started)
    {
        if (result?.Properties is null)
        {
            return Exit(provider, "CallbackWithoutResult", started, succeeded: false);
        }

        if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            return Exit(provider, DescribeMissingCode(result), started, succeeded: false);
        }

        result.Properties.TryGetValue("state", out var returnedState);

        // The shared completion page owns the exchange + token storage + auth-state refresh.
        // Reusing it keeps the single-use-code contract in exactly one place.
        var returnedStateQuery = string.IsNullOrEmpty(returnedState)
            ? string.Empty
            : $"&state={Uri.EscapeDataString(returnedState)}";
        _navigationManager.NavigateTo(
            $"/auth/oauth-complete?code={Uri.EscapeDataString(code)}{returnedStateQuery}");
        return Exit(provider, "CodeReceived", started, succeeded: true);
    }

    /// <summary>
    /// A completion the server could not finish comes back as <c>error=&lt;code&gt;</c>. The server's
    /// code is a fixed name and safe to log; anything else in that parameter is dropped, because a
    /// custom-scheme callback can be sent by another app.
    /// </summary>
    private static string DescribeMissingCode(WebAuthenticatorResult result) =>
        result.Properties.TryGetValue("error", out var error)
            && error is { Length: > 0 and <= 80 }
            && error.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                ? $"ServerError:{error}"
                : "CallbackWithoutCode";

    private bool Exit(string provider, string exit, long started, bool succeeded)
    {
        var elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogExit(_logger, provider, exit, elapsedMilliseconds);
        return succeeded;
    }

    // The type name only: this exception's message can quote the callback URI, code included.
    private bool LogUnexpectedExit(string provider, Exception exception, long started)
    {
        Exit(provider, exception.GetType().Name, started, succeeded: false);
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "External sign-in started for {Provider}")]
    private static partial void LogStarted(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "External sign-in for {Provider} ended with {Exit} after {ElapsedMilliseconds} ms")]
    private static partial void LogExit(ILogger logger, string provider, string exit, long elapsedMilliseconds);
}
