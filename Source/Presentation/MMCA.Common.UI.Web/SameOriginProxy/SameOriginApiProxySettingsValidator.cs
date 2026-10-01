using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Web.SameOriginProxy;

/// <summary>
/// Startup validation for <see cref="SameOriginApiProxySettings"/>, registered by
/// <c>AddCommonSameOriginApiProxy</c> with <c>ValidateOnStart</c>. The prefix becomes a route pattern
/// and the gateway address the destination of every proxied request, so both are refused unless they
/// are plain values; <c>SameSite=None</c> is refused because a cookie that authenticates data calls
/// must never ride on a cross-site request.
/// </summary>
internal sealed class SameOriginApiProxySettingsValidator : IValidateOptions<SameOriginApiProxySettings>
{
    private static readonly SearchValues<char> ForbiddenPathCharacters = SearchValues.Create("{}*?#\\");

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SameOriginApiProxySettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        const string section = SameOriginApiProxySettings.SectionName;

        if (!IsValidPrefix(options.PathPrefix))
        {
            failures.Add(
                $"{section}:PathPrefix '{options.PathPrefix}' is not a valid proxy path. It must start with '/', " +
                "must not end with '/', and must be a literal path such as '/api' (no route syntax, query, fragment or whitespace).");
        }

        if (!Uri.TryCreate(options.GatewayAddress, UriKind.Absolute, out var gateway) || string.IsNullOrEmpty(gateway.Host))
        {
            failures.Add(
                $"{section}:GatewayAddress (or Api:ApiEndpoint when it is unset) must be an absolute address such as " +
                "'https+http://gateway' or 'https://localhost:6001'; the proxy has no upstream to forward to.");
        }

        if (options.SessionCookieSameSite is not (SameSiteMode.Strict or SameSiteMode.Lax))
        {
            failures.Add($"{section}:SessionCookieSameSite must be Strict or Lax; the session cookie authenticates data calls.");
        }

        failures.AddRange(
            new[] { options.RefreshPath, options.RevokePath }
                .Concat(options.AdditionalTokenIssuingPaths)
                .Where(path => !IsValidRelativePath(path))
                .Select(path => $"{section}: the endpoint path '{path}' must be a non-empty relative path such as 'auth/login'."));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    internal static bool IsValidPrefix(string? prefix) =>
        !string.IsNullOrWhiteSpace(prefix)
        && prefix.Length > 1
        && prefix[0] == '/'
        && prefix[^1] != '/'
        && !prefix.Any(char.IsWhiteSpace)
        && !prefix.AsSpan().ContainsAny(ForbiddenPathCharacters);

    private static bool IsValidRelativePath(string? path) =>
        path is not null
        && !path.AsSpan().Trim('/').IsWhiteSpace()
        && !path.Any(char.IsWhiteSpace)
        && !path.AsSpan().ContainsAny(ForbiddenPathCharacters)
        && !path.Contains("://", StringComparison.Ordinal);
}
