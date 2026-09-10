using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Application.Auth.TwoFactor;

/// <summary>
/// Configuration for the time-based one-time-password second factor. Bound from the
/// <c>Authentication:TwoFactor</c> configuration section.
/// </summary>
/// <remarks>
/// The defaults are the RFC 6238 values every authenticator app assumes (six digits, a thirty-second
/// step). They are configurable because a host may have to match an existing enrollment base, not
/// because moving them is a good idea: an app keyed against one period cannot read codes minted with
/// another.
/// </remarks>
public sealed class TwoFactorSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Authentication:TwoFactor";

    /// <summary>
    /// The issuer shown beside the account in the authenticator app, and the label the provisioning
    /// URI is built from. Defaults to the application name where the host leaves it unset.
    /// </summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string Issuer { get; init; } = "MMCA";

    /// <summary>Number of digits in a generated code.</summary>
    [Range(6, 8)]
    public int Digits { get; init; } = 6;

    /// <summary>Length of one time step, in seconds.</summary>
    [Range(15, 120)]
    public int PeriodSeconds { get; init; } = 30;

    /// <summary>
    /// How many time steps on EACH side of the current one are accepted, to tolerate clock skew
    /// between the server and the user's phone.
    /// </summary>
    /// <remarks>
    /// One step (the default) widens the window to roughly ninety seconds, which is the usual
    /// trade-off: zero rejects a phone a few seconds out of step, and a large window multiplies how
    /// many codes are live at once for an attacker guessing.
    /// </remarks>
    [Range(0, 5)]
    public int VerificationWindowSteps { get; init; } = 1;

    /// <summary>How many single-use recovery codes are issued when enrollment is confirmed or the set is regenerated.</summary>
    [Range(1, 32)]
    public int RecoveryCodeCount { get; init; } = 10;

    /// <summary>
    /// Number of random bytes behind one recovery code before encoding. Ten bytes give an eighty-bit
    /// code, which is far past guessable while still being short enough to read off paper.
    /// </summary>
    [Range(8, 32)]
    public int RecoveryCodeByteLength { get; init; } = 10;

    /// <summary>Number of random bytes behind the shared secret. Twenty bytes is the RFC 4226 recommendation.</summary>
    [Range(16, 64)]
    public int SecretByteLength { get; init; } = 20;
}
