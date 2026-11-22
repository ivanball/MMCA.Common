using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Pages.Auth.Links;

/// <summary>
/// Reads the address and single-use token an emailed link carries (<c>email</c> and <c>token</c>
/// pairs, in the URL fragment or, as a fallback, the query string) for the confirm-email and
/// reset-password pages, whose links have the same shape. Pure: it holds no page state.
/// </summary>
internal static class EmailedLinkReader
{
    /// <summary>
    /// Merges an emailed link's fragment (or query) into the address and token a form holds.
    /// </summary>
    /// <param name="fragment">The raw fragment or query, without its leading character.</param>
    /// <param name="replace">
    /// <see langword="false"/> on the first render: only EMPTY values are filled (the query string and
    /// the first pair win). <see langword="true"/> for a new link opened on the open page: nothing the
    /// form held survives a fragment that carries either pair, so an old token is never used under a new
    /// address (Store Run 5 D3).
    /// </param>
    /// <param name="email">The address the form holds.</param>
    /// <param name="token">The token the form holds.</param>
    /// <returns>Whether the fragment carried an <c>email</c> or <c>token</c> pair, and the merged values.</returns>
    internal static (bool Recognized, string Email, string Token) MergeLinkFragment(string? fragment, bool replace, string? email, string? token)
    {
        email ??= string.Empty;
        token ??= string.Empty;
        if (string.IsNullOrWhiteSpace(fragment))
        {
            return (false, email, token);
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
                email = string.Empty;
                token = string.Empty;
            }

            recognized = true;
            (email, token) = FillEmpty(isEmail, value, email, token);
        }

        return (recognized, email, token);
    }

    /// <summary>
    /// The <c>email</c> and <c>token</c> pairs of <paramref name="location"/>'s query string, the
    /// fallback shape of an emailed link: the query without its leading <c>?</c> when it carries either
    /// pair, otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="location">The absolute location navigated to.</param>
    /// <returns>The query to merge with <see cref="MergeLinkFragment"/>, or <see langword="null"/>.</returns>
    internal static string? LinkQueryOf(string location)
    {
        var query = new Uri(location, UriKind.Absolute).Query;
        return query.Length > 1 && MergeLinkFragment(query[1..], replace: true, null, null).Recognized
            ? query[1..]
            : null;
    }

    /// <summary>
    /// Removes the <c>email</c> and <c>token</c> query parameters from the address bar and the history
    /// entry, as taking a fragment does, so the single-use token of a link read from the query string
    /// does not stay in the URL (Store Run 5 R6b). Other parameters and the fragment are kept.
    /// </summary>
    /// <param name="navigation">The page's navigation manager.</param>
    /// <param name="location">The absolute location that carried the link.</param>
    internal static void ScrubLinkQuery(NavigationManager navigation, string location) =>
        navigation.NavigateTo(
            navigation.GetUriWithQueryParameters(location, new Dictionary<string, object?>(StringComparer.Ordinal) { ["email"] = null, ["token"] = null }),
            replace: true);

    /// <summary>Whether <paramref name="location"/> carries a non-empty fragment.</summary>
    /// <param name="location">The absolute location navigated to.</param>
    /// <returns><see langword="true"/> when a fragment follows the hash.</returns>
    internal static bool HasFragment(string location)
    {
        var hash = location.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 && hash < location.Length - 1;
    }

    /// <summary>Puts <paramref name="value"/> into the address or the token, only when that one is still empty.</summary>
    /// <param name="isEmail">Whether the value is the address (otherwise the token).</param>
    /// <param name="value">The decoded value.</param>
    /// <param name="email">The address so far.</param>
    /// <param name="token">The token so far.</param>
    /// <returns>The address and token after the fill.</returns>
    private static (string Email, string Token) FillEmpty(bool isEmail, string value, string email, string token)
    {
        if (isEmail && string.IsNullOrWhiteSpace(email))
        {
            return (value, token);
        }

        if (!isEmail && string.IsNullOrWhiteSpace(token))
        {
            return (email, value);
        }

        return (email, token);
    }

    /// <summary>
    /// Reads one <c>name=value</c> pair of an emailed link's fragment: <see langword="true"/> for an
    /// <c>email</c> or <c>token</c> pair (names match exactly), with its value decoded.
    /// </summary>
    /// <param name="pair">One pair of the fragment.</param>
    /// <param name="isEmail">Whether the pair is the address (otherwise the token).</param>
    /// <param name="value">The decoded value.</param>
    /// <returns>Whether the pair is one the link carries.</returns>
    private static bool TryReadLinkPair(string pair, out bool isEmail, out string value)
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
}
