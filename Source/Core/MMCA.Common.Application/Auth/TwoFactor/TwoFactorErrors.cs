using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.TwoFactor;

/// <summary>
/// The failures the two-factor workflows return, declared once so a client, a test and the sign-in
/// flow all match on the same literals.
/// </summary>
/// <remarks>
/// <see cref="TwoFactorRequiredCode"/> is the one code a client MUST branch on: it is the signal to
/// prompt for a code and retry the same credentials, and it is deliberately distinct from
/// <c>Auth.InvalidCredentials</c>. That distinction leaks only to a caller who has already proved the
/// password, since the challenge runs after the password check.
/// </remarks>
public static class TwoFactorErrors
{
    /// <summary>Code returned when the account needs a second factor and the caller supplied none.</summary>
    public const string TwoFactorRequiredCode = "Authentication.TwoFactorRequired";

    /// <summary>Code returned when a second factor was supplied and did not verify.</summary>
    public const string TwoFactorInvalidCode = "Authentication.TwoFactorInvalid";

    /// <summary>Code returned when a two-factor action is attempted on an account that never enrolled.</summary>
    public const string TwoFactorNotEnrolledCode = "Authentication.TwoFactorNotEnrolled";

    /// <summary>Code returned when confirming an enrollment that was never started.</summary>
    public const string TwoFactorEnrollmentMissingCode = "Authentication.TwoFactorEnrollmentMissing";

    /// <summary>The "supply a code and retry" failure.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>An unauthorized error.</returns>
    public static Error TwoFactorRequired(string? source = null) => Error.Unauthorized(
        TwoFactorRequiredCode,
        "This account requires a second authentication factor. Supply the code from your authenticator app or a recovery code.",
        source);

    /// <summary>The "that code did not verify" failure.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>An unauthorized error.</returns>
    /// <remarks>
    /// One message for a wrong time-based code and for a wrong recovery code: telling the two apart
    /// would say whether a presented value was recovery-code shaped, which is a distinction the
    /// caller has no legitimate use for.
    /// </remarks>
    public static Error TwoFactorInvalid(string? source = null) => Error.Unauthorized(
        TwoFactorInvalidCode,
        "The two-factor code is invalid or has expired.",
        source);

    /// <summary>The "this account has no second factor" failure.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>A conflict error.</returns>
    public static Error TwoFactorNotEnrolled(string? source = null) => Error.Conflict(
        TwoFactorNotEnrolledCode,
        "Two-factor authentication is not enabled for this account.",
        source);

    /// <summary>The "start enrollment before confirming it" failure.</summary>
    /// <param name="source">The reporting member, for the error payload.</param>
    /// <returns>A conflict error.</returns>
    public static Error TwoFactorEnrollmentMissing(string? source = null) => Error.Conflict(
        TwoFactorEnrollmentMissingCode,
        "No two-factor enrollment is in progress for this account. Start one before confirming it.",
        source);
}
