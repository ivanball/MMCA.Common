using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Passkeys;

namespace MMCA.Common.Infrastructure.Auth.Passkeys;

/// <summary>
/// Startup validation for <see cref="PasskeySettings"/> (ADR-139). Disabled settings need nothing;
/// enabled settings need the relying party and at least one absolute origin, and every failure names
/// the configuration key to fix, so a misconfigured host fails at start rather than at the first
/// ceremony.
/// </summary>
internal sealed class PasskeySettingsValidator : IValidateOptions<PasskeySettings>
{
    /// <summary>Whether <paramref name="origin"/> is an absolute http(s) origin with no path, query or fragment.</summary>
    /// <param name="origin">The configured origin.</param>
    /// <returns><see langword="true"/> when the browser could report it as a ceremony origin.</returns>
    internal static bool IsAbsoluteOrigin(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PasskeySettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.RelyingPartyId))
        {
            failures.Add("Passkeys:RelyingPartyId is required when Passkeys:Enabled is true (the registrable domain, for example example.com).");
        }

        if (string.IsNullOrWhiteSpace(options.RelyingPartyName))
        {
            failures.Add("Passkeys:RelyingPartyName is required when Passkeys:Enabled is true.");
        }

        if (options.Origins is not { Count: > 0 })
        {
            failures.Add("Passkeys:Origins needs at least one origin when Passkeys:Enabled is true (for example https://app.example.com).");
        }
        else
        {
            failures.AddRange(options.Origins
                .Where(origin => !IsAbsoluteOrigin(origin))
                .Select(origin => $"Passkeys:Origins contains '{origin}', which is not an absolute origin such as https://app.example.com."));
        }

        if (string.IsNullOrWhiteSpace(options.DataSourceName))
        {
            failures.Add("Passkeys:DataSourceName is required.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
