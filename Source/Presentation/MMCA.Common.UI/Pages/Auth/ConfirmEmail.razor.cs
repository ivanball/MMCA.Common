using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Pages.Auth.Links;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the anonymous <c>/confirm-email</c> landing page (ADR-116): redeem the token from
/// a confirmation email, or ask for a fresh one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credentials arrive in the URI FRAGMENT.</b> The emailed link is composed as
/// <c>.../confirm-email#email=..&amp;token=..</c>, and browsers never send a fragment to a server,
/// so the live token stays out of ingress access logs, request telemetry and Referer headers. It can
/// only be read from JS, and <c>locationHashTake</c> also scrubs it from the address bar and the
/// history entry. Query-string parameters are honored as a fallback, and both fields stay editable so
/// the raw code from the message body can be typed by hand. This mirrors the reset-password page.
/// </para>
/// <para>
/// <b>One POST per link.</b> A link carrying both values is redeemed once, on the first interactive
/// render, through <see cref="IEmailConfirmationUIService"/>, which never retries: the token is
/// single-use, so a retried POST can only spend it twice. A link missing either value lands on
/// manual entry without posting a request that can only be refused; a refused token keeps the form
/// with the API's own message and offers a resend.
/// </para>
/// <para>
/// Anonymous by necessity: an account whose address is unconfirmed may not be able to sign in at
/// all when the host requires confirmed email.
/// </para>
/// </remarks>
public sealed partial class ConfirmEmail : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private ConfirmationState _state = ConfirmationState.Working;
    private string _email = string.Empty;
    private string _token = string.Empty;
    private string? _errorMessage;
    private bool _linkSent;
    private bool _isBusy;
    private bool _attempted;
    private bool _disposed;
    private bool _locationHandlerRegistered;

    /// <summary>What the page is showing.</summary>
    private enum ConfirmationState
    {
        /// <summary>Reading the link and redeeming its token.</summary>
        Working,

        /// <summary>Manual entry, with any refusal or resend notice above it.</summary>
        Form,

        /// <summary>The address is confirmed.</summary>
        Confirmed,
    }

    /// <summary>Gets or sets the address from the emailed link's query string, when it carries one.</summary>
    [SupplyParameterFromQuery(Name = "email")]
    public string? EmailFromQuery { get; set; }

    /// <summary>Gets or sets the token from the emailed link's query string, when it carries one.</summary>
    [SupplyParameterFromQuery(Name = "token")]
    public string? TokenFromQuery { get; set; }

    [Inject] private IEmailConfirmationUIService ConfirmationService { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private IStringLocalizer<SharedResource> L { get; set; } = default!;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_locationHandlerRegistered)
        {
            Navigation.LocationChanged -= OnLocationChanged;
        }

        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Listens for a new link opened in this tab while the page is already showing (for example after
    /// "send me a new one"): only the fragment or the query string changes, so the page is not rebuilt and
    /// <see cref="OnAfterRenderAsync"/> never runs its first-render read again.
    /// </remarks>
    protected override void OnInitialized()
    {
        Navigation.LocationChanged += OnLocationChanged;
        _locationHandlerRegistered = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only fills EMPTY fields, so a value the visitor corrected by hand is not overwritten when the
    /// component's parameters are set again.
    /// </remarks>
    protected override void OnParametersSet()
    {
        if (string.IsNullOrWhiteSpace(_email) && !string.IsNullOrWhiteSpace(EmailFromQuery))
        {
            _email = EmailFromQuery;
        }

        if (string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(TokenFromQuery))
        {
            _token = TokenFromQuery;
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _attempted)
        {
            return;
        }

        _attempted = true;
        ApplyFragment(await TakeFragmentAsync(), replace: false);
        await RedeemOrShowFormAsync();
    }

    /// <summary>
    /// A navigation within the page that carries a fragment or a query string with the link's pairs is
    /// a new emailed link (Store Run 5 D3, R6b): its values replace whatever the form held, it is
    /// redeemed once exactly like a fresh tab, and reading it scrubs the single-use token from the
    /// address bar.
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_disposed || !_attempted)
        {
            return;
        }

        if (EmailedLinkReader.HasFragment(e.Location))
        {
            _ = InvokeAsync(async () => await RedeemNewLinkAsync(await TakeFragmentAsync()));
        }
        else if (EmailedLinkReader.LinkQueryOf(e.Location) is { } query)
        {
            _ = InvokeAsync(() =>
            {
                EmailedLinkReader.ScrubLinkQuery(Navigation, e.Location);
                return RedeemNewLinkAsync(query);
            });
        }
    }

    /// <summary>Replaces the form's values with a new link's and redeems it once, as a fresh tab does.</summary>
    /// <param name="link">The link's fragment or query, without its leading character.</param>
    /// <returns>A task that completes when the link was redeemed or the form shown.</returns>
    private Task RedeemNewLinkAsync(string? link)
    {
        if (_disposed || !ApplyFragment(link, replace: true))
        {
            return Task.CompletedTask;
        }

        _linkSent = false;
        _errorMessage = null;
        _state = ConfirmationState.Working;
        return RedeemOrShowFormAsync();
    }

    private Task RedeemOrShowFormAsync()
    {
        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_token))
        {
            // Nothing to redeem: straight to manual entry and resend rather than posting a request
            // that can only be refused.
            _state = ConfirmationState.Form;
            StateHasChanged();
            return Task.CompletedTask;
        }

        return ConfirmAsync();
    }

    private async Task<string?> TakeFragmentAsync()
    {
        // Resolved rather than injected: the JS module is absent during SSR prerender, in a component
        // test and in a host without the capabilities module, and the query-string and manual-entry
        // paths still work without it.
        var module = Services.GetService<CapabilitiesJsModule>();

        return module is null
            ? null
            : await module.InvokeOrDefaultAsync<string?>("locationHashTake", [], CancellationToken.None);
    }

    /// <summary>Copies the <c>email</c> and <c>token</c> pairs out of the URL fragment into the fields.</summary>
    /// <param name="fragment">The raw fragment, without its leading hash.</param>
    /// <param name="replace">See <see cref="EmailedLinkReader.MergeLinkFragment"/>.</param>
    /// <returns><see langword="true"/> when the fragment carried an <c>email</c> or <c>token</c> pair.</returns>
    private bool ApplyFragment(string? fragment, bool replace)
    {
        var (recognized, email, token) = EmailedLinkReader.MergeLinkFragment(fragment, replace, _email, _token);
        _email = email;
        _token = token;
        return recognized;
    }

    private async Task ConfirmAsync()
    {
        _errorMessage = null;
        _linkSent = false;

        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_token))
        {
            _errorMessage = L["Auth.Confirm.EmailAndTokenRequired"].Value;
            _state = ConfirmationState.Form;
            return;
        }

        _isBusy = true;
#pragma warning disable BL0012 // Shows the busy state before the await below, and ConfirmAsync also runs from OnAfterRenderAsync, which does not re-render
        StateHasChanged();
#pragma warning restore BL0012

        try
        {
            var result = await ConfirmationService.ConfirmEmailAsync(_email.Trim(), _token.Trim(), _cts.LifetimeToken());
            if (result.IsSuccess)
            {
                _state = ConfirmationState.Confirmed;
                return;
            }

            // Every rejection collapses to one error on the server (unknown, expired, mismatched or
            // attempt-capped token, and a vanished account are indistinguishable), so the page shows
            // the API's own wording and offers a fresh link.
            _errorMessage = result.LocalizedErrorMessage(L) ?? L["Auth.Confirm.GenericError"].Value;
            _state = ConfirmationState.Form;
        }
        catch (OperationCanceledException)
        {
            // Expected during component disposal or an InteractiveAuto render-mode transition.
        }
        finally
        {
            _isBusy = false;
            if (!_disposed)
            {
#pragma warning disable BL0012 // ConfirmAsync also runs from OnAfterRenderAsync, which does not re-render once it completes
                StateHasChanged();
#pragma warning restore BL0012
            }
        }
    }

    private async Task ResendAsync()
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
            var result = await ConfirmationService.ResendEmailConfirmationAsync(_email.Trim(), _cts.LifetimeToken());
            if (result.IsSuccess)
            {
                // The endpoint answers 202 whether or not the address holds an unconfirmed account, so
                // the notice says exactly that and no more.
                _linkSent = true;
                return;
            }

            _errorMessage = result.LocalizedErrorMessage(L) ?? L["Auth.Confirm.GenericError"].Value;
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
}
