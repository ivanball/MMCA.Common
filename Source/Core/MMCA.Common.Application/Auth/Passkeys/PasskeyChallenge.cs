namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>One stored passkey challenge (ADR-139): which ceremony it opened, for whom, and the exact options issued.</summary>
/// <param name="Ceremony">Registration or sign-in (<see cref="Register"/> or <see cref="SignIn"/>).</param>
/// <param name="UserId">The account a registration was opened for; <see langword="null"/> for a sign-in.</param>
/// <param name="OptionsJson">The options as issued, which the ceremony checks the browser's response against.</param>
public sealed record PasskeyChallenge(string Ceremony, UserIdentifierType? UserId, string OptionsJson)
{
    /// <summary>The <see cref="Ceremony"/> of a registration.</summary>
    public const string Register = "register";

    /// <summary>The <see cref="Ceremony"/> of a sign-in.</summary>
    public const string SignIn = "signin";
}
