using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The errors the passkey endpoints answer with (ADR-139). A failed ceremony is deliberately vague:
/// an unknown credential, a bad signature and a counter that did not advance all read as
/// <see cref="AssertionFailedCode"/>, and the reason goes to the server log as a Warning instead.
/// </summary>
public static class PasskeyErrors
{
    /// <summary>Code of <see cref="NotSupported"/> (named apart from the sign-in-link constant, which the error-resource scan resolves by name).</summary>
    public const string PasskeysNotSupportedCode = "Passkeys.NotSupported";

    /// <summary>Code of <see cref="ChallengeInvalid"/>.</summary>
    public const string ChallengeInvalidCode = "Passkeys.ChallengeInvalid";

    /// <summary>Code of <see cref="RegistrationFailed"/>.</summary>
    public const string RegistrationFailedCode = "Passkeys.RegistrationFailed";

    /// <summary>Code of <see cref="AssertionFailed"/>.</summary>
    public const string AssertionFailedCode = "Passkeys.AssertionFailed";

    /// <summary>Code of <see cref="StepUpRequired"/>.</summary>
    public const string StepUpRequiredCode = "Passkeys.StepUpRequired";

    /// <summary>Code of <see cref="LastPasskey"/>.</summary>
    public const string LastPasskeyCode = "Passkeys.LastPasskey";

    /// <summary>Code of <see cref="NotFound"/>.</summary>
    public const string NotFoundCode = "Passkeys.NotFound";

    /// <summary>The app's authentication service has not adopted passkey sign-in.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error NotSupported(string? source = null) =>
        Error.Failure(PasskeysNotSupportedCode, "Passkey sign-in is not supported.", source);

    /// <summary>The flow is unknown, expired, already used, or belongs to another ceremony or account.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error ChallengeInvalid(string? source = null) =>
        Error.Validation(ChallengeInvalidCode, "The passkey request expired or was already used. Try again.", source);

    /// <summary>The attestation did not verify, or the credential is already registered.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error RegistrationFailed(string? source = null) =>
        Error.Validation(RegistrationFailedCode, "The passkey could not be registered.", source);

    /// <summary>The assertion did not sign the user in (unknown credential, bad signature, counter).</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error AssertionFailed(string? source = null) =>
        Error.Unauthorized(AssertionFailedCode, "The passkey could not sign you in.", source);

    /// <summary>
    /// Registering a passkey needs a recent sign-in: an access token under ten minutes old, or one
    /// minted after a second factor.
    /// </summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error StepUpRequired(string? source = null) =>
        Error.Forbidden(StepUpRequiredCode, "Sign in again before adding a passkey.", source);

    /// <summary>The passkey is the account's only way to sign in, so it cannot be deleted.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error LastPasskey(string? source = null) =>
        Error.Conflict(LastPasskeyCode, "This passkey is the only way to sign in to this account, so it cannot be deleted.", source);

    /// <summary>No passkey with that id belongs to the caller.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string? source = null) =>
        Error.NotFoundError(NotFoundCode, "The passkey was not found.", source);
}
