using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Navigation;
using MudBlazor;

namespace MMCA.Common.UI.Pages.Auth.SignInLinks;

/// <summary>
/// Code-behind for the anonymous <c>/auth/sign-in-link</c> landing page (ADR-116): redeem the
/// single-use link from a sign-in or invitation email, or ask for a fresh one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credentials arrive in the URI FRAGMENT</b>, exactly as on <c>/confirm-email</c>: browsers
/// never send a fragment to a server, so the live token stays out of access logs, telemetry and
/// Referer headers, and <c>locationHashTake</c> scrubs it from the address bar and the history entry.
/// </para>
/// <para>
/// <b>Redeeming waits for a tap on Continue.</b> Unlike the confirmation page, which redeems on first
/// render, this token opens a session: a mail scanner that renders the page with script enabled would
/// otherwise spend the token and sign itself in. The tap also carries the Terms acceptance the server
/// records when an invitation creates the account.
/// </para>
/// <para>
/// One POST per tap, through <see cref="ISignInLinkUIService"/>, which never retries: the token is
/// single-use, so a retried POST can only spend it twice.
/// </para>
/// <para>
/// <b>A second link opened on the open page is read too</b> (U-48): on MAUI an app link that lands on
/// this route while the page is showing (the in-page "Email me a new link" flow) is a same-page
/// navigation, which renders nothing new, so the page reads the fragment of every location change to
/// its own route and waits for Continue on the new link. A token already posted is never shown again.
/// </para>
/// </remarks>
public sealed partial class SignInLink : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private LinkState _state = LinkState.Reading;
    private string _email = string.Empty;
    private string _token = string.Empty;
    private string? _errorMessage;
    private bool _linkSent;
    private bool _isBusy;
    private bool _attempted;
    private bool _disposed;

    // The route this page was opened on, and the last token Continue posted: a link that brings that
    // token back is ignored rather than posted twice.
    private string _ownPath = string.Empty;
    private string? _submittedToken;

    /// <summary>What the page is showing.</summary>
    private enum LinkState
    {
        /// <summary>Reading the link from the fragment.</summary>
        Reading,

        /// <summary>The link is complete; waiting for Continue.</summary>
        Ready,

        /// <summary>The link was incomplete or refused; offering a fresh one.</summary>
        Failed,
    }

    [Inject] private ISignInLinkUIService SignInLinks { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private IOptions<SignInSettings> SignInOptions { get; set; } = default!;

    [Inject] private IOptions<LegalSettings> LegalOptions { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    private LegalSettings Legal => LegalOptions.Value;

    // The host opted into terms acceptance by publishing a Terms URL (the register page's rule).
    private bool RequiresTerms => !string.IsNullOrWhiteSpace(Legal.TermsUrl);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Navigation.LocationChanged -= OnLocationChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _ownPath = new Uri(Navigation.Uri).AbsolutePath;
        Navigation.LocationChanged += OnLocationChanged;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _attempted)
        {
            return;
        }

        _attempted = true;
        ShowLink(await TakeFragmentAsync());
        StateHasChanged();
    }

    /// <summary>
    /// A link opened while this page is already showing navigates to the same route with a new
    /// fragment, which re-renders nothing; read it here. Before the first read the page has not taken a
    /// fragment yet, so that read picks the new one up itself.
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var location = new Uri(e.Location, UriKind.Absolute);
        if (!_attempted
            || location.Fragment.Length <= 1
            || !string.Equals(location.AbsolutePath, _ownPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = InvokeAsync(() => ShowNewLinkAsync(location.Fragment[1..]));
    }

    private async Task ShowNewLinkAsync(string fragment)
    {
        // A redemption in flight owns the page; it ends on the landing page or on a refusal.
        if (_disposed || _isBusy)
        {
            return;
        }

        // Scrub the new link from the address bar and the history entry, as the first read does; the
        // values come from the navigation itself, so a host without the module still reads them.
        await TakeFragmentAsync();

        var (_, token) = ParseFragment(fragment);
        if (_disposed || string.Equals(token.Trim(), _submittedToken, StringComparison.Ordinal))
        {
            return;
        }

        _linkSent = false;
        ShowLink(fragment);
        StateHasChanged();
    }

    /// <summary>
    /// Takes the address and the token from <paramref name="fragment"/> and offers Continue, or the
    /// fresh-link form when either is missing.
    /// </summary>
    /// <param name="fragment">The raw fragment, without its leading hash.</param>
    private void ShowLink(string? fragment)
    {
        (_email, _token) = ParseFragment(fragment);

        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_token))
        {
            _errorMessage = L["Auth.SignInLink.Incomplete"].Value;
            _state = LinkState.Failed;
        }
        else
        {
            _errorMessage = null;
            _state = LinkState.Ready;
        }
    }

    // A document name inside the consent sentence: a link when the host published that document,
    // plain text when it did not (the register page's helper).
    private static RenderFragment LegalDocument(string url, string name) => builder =>
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            builder.AddContent(0, name);
            return;
        }

        builder.OpenComponent<ExternalLink>(1);
        builder.AddComponentParameter(2, nameof(ExternalLink.Href), url);
        builder.AddComponentParameter(3, nameof(ExternalLink.Underline), Underline.Always);
        builder.AddComponentParameter(4, nameof(ExternalLink.ChildContent), (RenderFragment)(content => content.AddContent(5, name)));
        builder.CloseComponent();
    };

    private async Task<string?> TakeFragmentAsync()
    {
        // Resolved rather than injected: the JS module is absent during SSR prerender, in a component
        // test and in a host without the capabilities module.
        var module = Services.GetService<CapabilitiesJsModule>();

        return module is null
            ? null
            : await module.InvokeOrDefaultAsync<string?>("locationHashTake", [], CancellationToken.None);
    }

    /// <summary>Reads the <c>email</c> and <c>token</c> pairs out of the URL fragment.</summary>
    /// <param name="fragment">The raw fragment, without its leading hash.</param>
    /// <returns>The address and the token, each empty when the fragment does not carry it.</returns>
    private static (string Email, string Token) ParseFragment(string? fragment)
    {
        var email = string.Empty;
        var token = string.Empty;
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return (email, token);
        }

        foreach (var pair in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator];
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);

            if (string.Equals(name, "email", StringComparison.Ordinal))
            {
                email = value;
            }
            else if (string.Equals(name, "token", StringComparison.Ordinal))
            {
                token = value;
            }
        }

        return (email, token);
    }

    private async Task RedeemAsync()
    {
        // One POST per token: a second tap, or the same link opened again, never spends it twice.
        var token = _token.Trim();
        if (_isBusy || string.Equals(token, _submittedToken, StringComparison.Ordinal))
        {
            return;
        }

        _submittedToken = token;
        _errorMessage = null;
        _isBusy = true;

        try
        {
            var result = await SignInLinks.RedeemSignInLinkAsync(_email.Trim(), token, RequiresTerms, _cts.LifetimeToken());
            if (result.IsSuccess)
            {
                // A full load, as after a password sign-in, so every component re-reads the new
                // auth state.
                Navigation.NavigateTo(ReturnUrlProtector.Sanitize(SignInOptions.Value.SignInLinkLandingPath, "/"), forceLoad: true);
                return;
            }

            // The token is spent or was never valid either way, so the only way on is a fresh link.
            _token = string.Empty;
            _errorMessage = RefusalMessage(result);
            _state = LinkState.Failed;
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task RequestNewLinkAsync()
    {
        _errorMessage = null;
        _linkSent = false;

        if (string.IsNullOrWhiteSpace(_email))
        {
            _errorMessage = L["Auth.Confirm.EmailRequired"].Value;
            return;
        }

        _isBusy = true;

        try
        {
            var result = await SignInLinks.RequestSignInLinkAsync(_email.Trim(), _cts.LifetimeToken());
            if (result.IsSuccess)
            {
                _linkSent = true;
                return;
            }

            _errorMessage = result.LocalizedErrorMessage(L) ?? L["Auth.SignInLink.GenericError"].Value;
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>
    /// Words for a refused redemption: the code's own resource when it has one (an invalid link, an
    /// account that needs its second factor), otherwise the generic refusal.
    /// </summary>
    /// <param name="result">The failed redemption.</param>
    /// <returns>The message for the alert.</returns>
    private string RefusalMessage(Result result)
    {
        if (result.Errors is [var error] && !string.IsNullOrEmpty(error.Code))
        {
            var byCode = L[error.Code];
            if (!byCode.ResourceNotFound)
            {
                return byCode.Value;
            }
        }

        return L["Auth.SignInLink.GenericError"].Value;
    }
}
