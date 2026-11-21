using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the <c>/reset-password</c> page: reading the emailed link's fragment, on the first
/// render and again for a new link opened on the open page (Store Run 5 D3).
/// </summary>
public partial class ResetPassword : IAsyncDisposable
{
    /// <summary>Drops the location handler.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_locationHandlerRegistered)
        {
            _locationHandlerRegistered = false;
            Navigation.LocationChanged -= OnLocationChanged;
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A new link opened in this tab while the page is already showing (for example after "send me a new
    /// one") only changes the fragment, so the page is not rebuilt and the first-render read never runs
    /// again. Its values replace the stale ones, exactly as in a fresh tab, and taking it scrubs the new
    /// single-use token from the address bar.
    /// </remarks>
    protected override void OnInitialized()
    {
        Navigation.LocationChanged += OnLocationChanged;
        _locationHandlerRegistered = true;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var hash = e.Location.IndexOf('#', StringComparison.Ordinal);
        if (_disposed || !_fragmentRead || hash < 0 || hash == e.Location.Length - 1)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            if (_disposed || !ApplyFragment(await TakeFragmentAsync(), replace: true))
            {
                return;
            }

            _isCompleted = false;
            _errorMessage = null;
            StateHasChanged();
        });
    }

    /// <summary>Reads the fragment through the capabilities module, which also scrubs it; null without the module.</summary>
    private async Task<string?> TakeFragmentAsync()
    {
        var module = Services.GetService<CapabilitiesJsModule>();
        return module is null
            ? null
            : await module.InvokeOrDefaultAsync<string?>("locationHashTake", [], CancellationToken.None);
    }

    /// <summary>Copies the link's address and token into the form (see <see cref="ConfirmEmail.MergeLinkFragment"/>).</summary>
    private bool ApplyFragment(string? fragment, bool replace)
    {
        var (recognized, email, token) = ConfirmEmail.MergeLinkFragment(fragment, replace, _model.Email, _model.Token);
        if (recognized)
        {
            _model.Email = email;
            _model.Token = token;
        }

        return recognized;
    }
}
