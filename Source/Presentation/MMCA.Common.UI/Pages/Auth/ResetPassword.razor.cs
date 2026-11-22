using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Pages.Auth.Links;
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
    /// one") only changes the fragment or the query string, so the page is not rebuilt and the
    /// first-render read never runs again. Its values replace the stale ones, exactly as in a fresh tab,
    /// and reading it scrubs the new single-use token from the address bar.
    /// </remarks>
    protected override void OnInitialized()
    {
        Navigation.LocationChanged += OnLocationChanged;
        _locationHandlerRegistered = true;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        if (_disposed || !_fragmentRead)
        {
            return;
        }

        var hash = e.Location.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0 && hash < e.Location.Length - 1)
        {
            _ = InvokeAsync(async () => PrefillNewLink(await TakeFragmentAsync()));
        }
        else if (EmailedLinkReader.LinkQueryOf(e.Location) is { } query)
        {
            // A link that differs only in its query string (Store Run 5 R6b): read it the same way, and
            // take its single-use token out of the address bar as taking a fragment does.
            _ = InvokeAsync(() =>
            {
                EmailedLinkReader.ScrubLinkQuery(Navigation, e.Location);
                PrefillNewLink(query);
            });
        }
    }

    /// <summary>Replaces the form's address and token with a new link's, as a fresh tab prefills them.</summary>
    /// <param name="link">The link's fragment or query, without its leading character.</param>
    private void PrefillNewLink(string? link)
    {
        if (_disposed || !ApplyFragment(link, replace: true))
        {
            return;
        }

        _isCompleted = false;
        _errorMessage = null;
        StateHasChanged();
    }

    /// <summary>Reads the fragment through the capabilities module, which also scrubs it; null without the module.</summary>
    private async Task<string?> TakeFragmentAsync()
    {
        var module = Services.GetService<CapabilitiesJsModule>();
        return module is null
            ? null
            : await module.InvokeOrDefaultAsync<string?>("locationHashTake", [], CancellationToken.None);
    }

    /// <summary>Copies the link's address and token into the form (see <see cref="EmailedLinkReader.MergeLinkFragment"/>).</summary>
    private bool ApplyFragment(string? fragment, bool replace)
    {
        var (recognized, email, token) = EmailedLinkReader.MergeLinkFragment(fragment, replace, _model.Email, _model.Token);
        if (recognized)
        {
            _model.Email = email;
            _model.Token = token;
        }

        return recognized;
    }
}
