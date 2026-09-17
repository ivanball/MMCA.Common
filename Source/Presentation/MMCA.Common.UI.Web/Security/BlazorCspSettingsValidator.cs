using System.Buffers;
using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Web.Security;

/// <summary>
/// Startup validation for <see cref="BlazorCspSettings"/>, registered by <c>AddCommonBlazorCsp()</c>
/// with <c>ValidateOnStart</c>. A frame source is spliced verbatim into a security response header, so
/// anything that is not a plain https origin is refused: a quote, semicolon or whitespace could smuggle
/// a keyword or a whole new directive into the policy, and a wildcard or bare scheme would widen
/// <c>frame-src</c> to arbitrary sites.
/// </summary>
internal sealed class BlazorCspSettingsValidator : IValidateOptions<BlazorCspSettings>
{
    /// <summary>
    /// Characters that could break out of a CSP source expression or widen it, plus the URL delimiters
    /// (user info, query, fragment) an origin never carries.
    /// </summary>
    private static readonly SearchValues<char> ForbiddenCharacters = SearchValues.Create("*'\";,@?#");

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BlazorCspSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = options.FrameSources
            .Where(source => !IsValidOrigin(source))
            .Select(source =>
                $"{BlazorCspSettings.SectionName}:FrameSources entry '{source}' is not a valid frame source. " +
                "Each entry must be an absolute https origin such as 'https://maps.example.com' " +
                "(no path, query, fragment, user info, wildcard, quote, semicolon, comma or whitespace).")
            .ToList();

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="source"/> is a plain https origin.</summary>
    internal static bool IsValidOrigin(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || source.Any(char.IsWhiteSpace)
            || source.AsSpan().ContainsAny(ForbiddenCharacters)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // The forbidden set already rules out user info ('@'), a query ('?') and a fragment ('#'), so a
        // root-only path is the last thing separating an origin from a URL.
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(uri.Host)
            && string.Equals(uri.PathAndQuery, "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The canonical CSP form of a validated origin: <c>scheme://host[:port]</c>, lower-cased host,
    /// default port dropped, no trailing slash.
    /// </summary>
    internal static string ToOrigin(string source) =>
        new Uri(source, UriKind.Absolute).GetLeftPart(UriPartial.Authority);
}
