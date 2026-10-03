using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.Application.Auth.Legal;

/// <summary>
/// The rules of the Terms of Service acceptance, held once so the registration flow, the API
/// controller base and any other caller apply them identically and a consumer cannot forget one.
/// </summary>
public static class LegalAcceptancePolicy
{
    /// <summary>
    /// The configured current terms version, trimmed, or <see langword="null"/> when none is configured
    /// (null options, null value, or whitespace).
    /// </summary>
    /// <param name="options">The bound options; may be <see langword="null"/> for a host that never opted in.</param>
    /// <returns>The current version, or <see langword="null"/>.</returns>
    public static string? ResolveCurrentVersion(LegalAcceptanceOptions? options)
    {
        var configured = options?.CurrentTermsVersion;
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    /// <summary>
    /// Checks that an acceptance names the configured current version. Fails when no version is
    /// configured (there is nothing to accept) and when the supplied version differs (ordinal), so a
    /// client that showed stale terms cannot record consent to the new ones.
    /// </summary>
    /// <param name="currentVersion">The resolved current version (see <see cref="ResolveCurrentVersion"/>).</param>
    /// <param name="suppliedVersion">The version the client says the user accepted.</param>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>Success, or <see cref="LegalAcceptanceErrors.VersionNotCurrent"/>.</returns>
    public static Result EnsureAcceptsCurrentVersion(string? currentVersion, string? suppliedVersion, string? source = null) =>
        currentVersion is not null && string.Equals(suppliedVersion?.Trim(), currentVersion, StringComparison.Ordinal)
            ? Result.Success()
            : Result.Failure(LegalAcceptanceErrors.VersionNotCurrent(source));

    /// <summary>
    /// Re-derives a standing against the configured current version, so the answer never depends on
    /// a consumer filling <see cref="LegalAcceptanceDTO.CurrentVersion"/> or
    /// <see cref="LegalAcceptanceDTO.IsCurrent"/> correctly: only the accepted version and instant are
    /// taken from <paramref name="standing"/>.
    /// </summary>
    /// <param name="currentVersion">The resolved current version.</param>
    /// <param name="standing">The consumer's answer.</param>
    /// <returns>The normalized standing.</returns>
    public static LegalAcceptanceDTO Normalize(string? currentVersion, LegalAcceptanceDTO standing)
    {
        ArgumentNullException.ThrowIfNull(standing);

        return LegalAcceptanceDTO.Evaluate(currentVersion, standing.AcceptedVersion, standing.AcceptedOn);
    }
}
