using Microsoft.AspNetCore.Components;
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
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _attempted)
        {
            return;
        }

        _attempted = true;
        ApplyFragment(await TakeFragmentAsync());

        if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrWhiteSpace(_token))
        {
            _errorMessage = L["Auth.SignInLink.Incomplete"].Value;
            _state = LinkState.Failed;
        }
        else
        {
            _state = LinkState.Ready;
        }

        StateHasChanged();
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

    /// <summary>Copies the <c>email</c> and <c>token</c> pairs out of the URL fragment.</summary>
    /// <param name="fragment">The raw fragment, without its leading hash.</param>
    private void ApplyFragment(string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return;
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
                _email = value;
            }
            else if (string.Equals(name, "token", StringComparison.Ordinal))
            {
                _token = value;
            }
        }
    }

    private async Task RedeemAsync()
    {
        _errorMessage = null;
        _isBusy = true;

        try
        {
            var result = await SignInLinks.RedeemSignInLinkAsync(_email.Trim(), _token.Trim(), RequiresTerms, _cts.LifetimeToken());
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
