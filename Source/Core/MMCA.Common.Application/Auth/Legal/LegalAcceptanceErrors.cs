using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.Application.Auth.Legal;

/// <summary>
/// The failures the Terms of Service acceptance returns, declared once so the registration gate, the
/// accept endpoint and a client all match on the same literals.
/// </summary>
public static class LegalAcceptanceErrors
{
    /// <summary>Code returned at registration when a terms version is configured and the box was not ticked.</summary>
    public const string TermsNotAcceptedCode = AuthErrorCodes.TermsNotAccepted;

    /// <summary>Code returned when an acceptance names a version other than the configured current one.</summary>
    public const string VersionNotCurrentCode = LegalAcceptanceErrorCodes.VersionNotCurrent;

    /// <summary>The registration refusal for a request that did not accept the terms.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>A validation error.</returns>
    public static Error TermsNotAccepted(string? source = null) => Error.Validation(
        TermsNotAcceptedCode,
        "You must accept the Terms of Service to register.",
        source);

    /// <summary>
    /// The refusal for an acceptance of a version that is not the configured current one, including
    /// any acceptance while no version is configured.
    /// </summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>A validation error.</returns>
    public static Error VersionNotCurrent(string? source = null) => Error.Validation(
        VersionNotCurrentCode,
        "The terms you accepted are no longer the current version. Please review the latest terms.",
        source);
}
