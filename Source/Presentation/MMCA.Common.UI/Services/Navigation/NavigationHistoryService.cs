using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MMCA.Common.UI.Services.Navigation;

/// <summary>
/// Per-circuit scoped service that bridges Blazor's <see cref="NavigationManager"/>
/// with the browser's history API for back-navigation. Used by detail pages and
/// the MAUI hardware-back bridge to perform a real <c>history.back()</c> when an
/// in-history previous entry exists, falling back to a hard-coded path otherwise.
/// </summary>
public sealed class NavigationHistoryService(NavigationManager navigation, IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MMCA.Common.UI/nav-interop.js";

    private readonly LazyJsModule _module = new(js, ModulePath);

    /// <summary>
    /// Returns <see langword="true"/> when the browser history stack contains a
    /// previous entry that <c>history.back()</c> would navigate to. Returns
    /// <see langword="false"/> during SSR prerender or when JS interop is unavailable.
    /// </summary>
    public async ValueTask<bool> CanGoBackAsync()
    {
        try
        {
            var module = await GetModuleAsync().ConfigureAwait(false);
            if (module is null)
            {
                return false;
            }

            var length = await module.InvokeAsync<int>("historyLength").ConfigureAwait(false);
            return length > 1;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (JSDisconnectedException)
        {
            return false;
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <summary>
    /// Navigates back one history entry when possible, otherwise navigates to
    /// <paramref name="fallback"/>. Use this from "Back" buttons on detail pages
    /// to honour the user's actual navigation source instead of a hard-coded route.
    /// </summary>
    public async ValueTask GoBackAsync(string fallback = "/")
    {
        if (await CanGoBackAsync().ConfigureAwait(false))
        {
            try
            {
                var module = await GetModuleAsync().ConfigureAwait(false);
                if (module is not null)
                {
                    await module.InvokeVoidAsync("historyBack").ConfigureAwait(false);
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Fall through to NavigateTo.
            }
            catch (JSDisconnectedException)
            {
                // Fall through to NavigateTo.
            }
            catch (JSException)
            {
                // Fall through to NavigateTo.
            }
        }

        navigation.NavigateTo(ReturnUrlProtector.Sanitize(fallback));
    }

    private async ValueTask<IJSObjectReference?> GetModuleAsync()
    {
        try
        {
            return await _module.GetOrImportAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _module.DisposeAsync();
}
