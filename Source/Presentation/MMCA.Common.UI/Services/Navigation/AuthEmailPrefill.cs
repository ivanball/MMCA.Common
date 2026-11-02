using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Services.Navigation;

/// <summary>
/// Carries an email address from one shared auth page to the next in the navigation's HISTORY STATE,
/// never in the URL, so the address stays out of the address bar, server logs and Referer headers.
/// Register uses it to land a duplicate address on sign-in, and sign-in to open Forgot password
/// with the address already typed.
/// </summary>
internal static class AuthEmailPrefill
{
    private const string ExistingAccountPrefix = "mmca-auth:existing-account:";
    private const string PrefillPrefix = "mmca-auth:prefill:";

    /// <summary>Navigates to <paramref name="path"/>, handing it <paramref name="email"/> to prefill.</summary>
    /// <param name="navigation">The navigation manager.</param>
    /// <param name="path">The local route to open.</param>
    /// <param name="email">The address to prefill.</param>
    /// <param name="existingAccount">
    /// Whether the address was just refused because it already holds an account, so the target can say so.
    /// </param>
    public static void NavigateWithEmail(NavigationManager navigation, string path, string email, bool existingAccount = false) =>
        navigation.NavigateTo(path, new NavigationOptions
        {
            HistoryEntryState = (existingAccount ? ExistingAccountPrefix : PrefillPrefix) + email,
        });

    /// <summary>Reads the address a previous page handed over, if any.</summary>
    /// <param name="navigation">The navigation manager.</param>
    /// <param name="existingAccount">Whether the address was refused as already registered.</param>
    /// <returns>The address, or <see langword="null"/> when the navigation carried none.</returns>
    public static string? Read(NavigationManager navigation, out bool existingAccount)
    {
        var state = navigation.HistoryEntryState;
        existingAccount = false;

        if (state is null)
        {
            return null;
        }

        if (state.StartsWith(ExistingAccountPrefix, StringComparison.Ordinal))
        {
            existingAccount = true;
            return NonEmpty(state[ExistingAccountPrefix.Length..]);
        }

        return state.StartsWith(PrefillPrefix, StringComparison.Ordinal)
            ? NonEmpty(state[PrefillPrefix.Length..])
            : null;
    }

    private static string? NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
