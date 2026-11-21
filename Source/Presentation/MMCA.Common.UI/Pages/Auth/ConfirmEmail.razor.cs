using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Common;
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

    /// <summary>
    /// Reads one <c>name=value</c> pair of an emailed link's fragment: <see langword="true"/> for an
    /// <c>email</c> or <c>token</c> pair (names match exactly), with its value decoded. Shared with the
    /// reset-password page, whose link has the same shape.
    /// </summary>
    /// <param name="pair">One pair of the fragment.</param>
    /// <param name="isEmail">Whether the pair is the address (otherwise the token).</param>
    /// <param name="value">The decoded value.</param>
    /// <returns>Whether the pair is one the link carries.</returns>
    internal static bool TryReadLinkPair(string pair, out bool isEmail, out string value)
    {
        isEmail = false;
        value = string.Empty;
        var separator = pair.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var name = pair[..separator];
        isEmail = string.Equals(name, "email", StringComparison.Ordinal);
        if (!isEmail && !string.Equals(name, "token", StringComparison.Ordinal))
        {
            return false;
        }

        value = Uri.UnescapeDataString(pair[(separator + 1)..]);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Listens for a new link opened in this tab while the page is already showing (for example after
    /// "send me a new one"): the browser only changes the fragment, so the page is not rebuilt and
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
    /// A navigation within the page that carries a fragment is a new emailed link (Store Run 5 D3): its
    /// values replace whatever the form held, it is redeemed once exactly like a fresh tab, and taking
    /// it scrubs the single-use token from the address bar.
    /// </summary>
    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_disposed || !_attempted || !HasFragment(e.Location))
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            var fragment = await TakeFragmentAsync();
            if (_disposed || !ApplyFragment(fragment, replace: true))
            {
                return;
            }

            _linkSent = false;
            _errorMessage = null;
            _state = ConfirmationState.Working;
            await RedeemOrShowFormAsync();
        });
    }

    private static bool HasFragment(string location)
    {
        var hash = location.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 && hash < location.Length - 1;
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
    /// <param name="replace">
    /// <see langword="false"/> on the first render: only EMPTY fields are filled (the query string and
    /// the first pair win). <see langword="true"/> for a new link opened on the open page: the link's
    /// values replace the stale ones, and a field the link does not carry is cleared, so an old token
    /// is never redeemed under a new address.
    /// </param>
    /// <returns><see langword="true"/> when the fragment carried an <c>email</c> or <c>token</c> pair.</returns>
    private bool ApplyFragment(string? fragment, bool replace)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return false;
        }

        var recognized = false;
        foreach (var pair in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryReadLinkPair(pair, out var isEmail, out var value))
            {
                continue;
            }

            if (replace && !recognized)
            {
                // A new link: nothing the form held survives it.
                _email = string.Empty;
                _token = string.Empty;
            }

            recognized = true;

            if (isEmail && string.IsNullOrWhiteSpace(_email))
            {
                _email = value;
            }
            else if (!isEmail && string.IsNullOrWhiteSpace(_token))
            {
                _token = value;
            }
        }

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
