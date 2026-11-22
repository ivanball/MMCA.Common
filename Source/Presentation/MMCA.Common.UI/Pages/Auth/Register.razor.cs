using Microsoft.AspNetCore.Components;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the <c>/register</c> page: the optional postal address block, which the host
/// can turn off through <c>RegistrationSettings.CollectAddress</c>, and the page lifetime that
/// cancels an in-flight registration.
/// </summary>
public partial class Register : IDisposable
{
    // Cancelled on dispose, so a registration still in flight when the user navigates away (or an
    // InteractiveAuto render-mode transition tears the page down) stops instead of completing into
    // a component that is gone.
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// The address an account was just registered with behind the email gate (no session opened);
    /// set, it replaces the form with the "check your email" state.
    /// </summary>
    private string? _pendingEmail;

    private ElementReference _pendingHeading;

    /// <summary>Moves focus to the "check your email" heading on the next render.</summary>
    private bool _focusPending;

    /// <summary>Gets a value indicating whether the host offers the optional address block at all.</summary>
    private bool CollectAddress => RegistrationOptions.Value.CollectAddress;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Cancels any in-flight registration; safe to call more than once.</summary>
    /// <param name="disposing">Whether managed state is being released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
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
        // The form is replaced in place, so focus moves to the new heading, or a keyboard or
        // screen-reader user would stay on the submit button that no longer exists.
        if (_focusPending && _pendingEmail is not null)
        {
            _focusPending = false;
            await _pendingHeading.FocusAsync();
        }
    }

    /// <summary>Shows the "check your email" state for <paramref name="email"/> instead of navigating.</summary>
    /// <param name="email">The address the account was registered with.</param>
    private void ShowPending(string email)
    {
        _pendingEmail = email.Trim();
        _errorMessage = null;
        _focusPending = true;
    }

    /// <summary>Opens the sign-in page with the registered address filled in (history state, never the URL).</summary>
    private void GoToSignIn() =>
        AuthEmailPrefill.NavigateWithEmail(Navigation, "/login", _pendingEmail ?? string.Empty);

    /// <summary>
    /// Null means "no address entered", which is legitimate: the address block is optional. Anything
    /// the user DID type is validated, and an invalid address blocks the registration instead of
    /// being silently dropped (the account used to be created with no address at all). With address
    /// collection off there is no block to type into, so there is nothing to validate or send.
    /// </summary>
    /// <returns>The address result, or null when there is no address to send.</returns>
    private Result<Address>? BuildAddressResult()
    {
        if (!CollectAddress)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_model.AddressLine1)
            && string.IsNullOrWhiteSpace(_model.AddressLine2)
            && string.IsNullOrWhiteSpace(_model.City)
            && string.IsNullOrWhiteSpace(_model.State)
            && string.IsNullOrWhiteSpace(_model.ZipCode)
            && string.IsNullOrWhiteSpace(_model.Country))
        {
            return null;
        }

        return Address.Create(_model.AddressLine1, _model.AddressLine2, _model.City, _model.State, _model.ZipCode, _model.Country);
    }
}
