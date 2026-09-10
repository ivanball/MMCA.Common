namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for email/password authentication.
/// </summary>
/// <param name="Email">The user's email address.</param>
/// <param name="Password">The user's password (transmitted over TLS, never logged).</param>
public readonly record struct LoginRequest(
    string Email,
    string Password)
{
    /// <summary>
    /// The second-factor code, when the account has two-factor authentication enabled: either the
    /// code from the authenticator app or one of the account's single-use recovery codes.
    /// </summary>
    /// <remarks>
    /// Declared as an init-only property rather than a third positional parameter on purpose. A
    /// positional parameter would widen the generated <c>Deconstruct</c>, which is a source break for
    /// any consumer that destructures a login request; a property leaves the constructor, the
    /// deconstruction and the JSON shape of every existing caller untouched, and a client that never
    /// sends the field simply leaves it null.
    /// <para>
    /// Omitting it on the first attempt is the normal flow: sign-in answers
    /// <c>Authentication.TwoFactorRequired</c> when the account needs a code and none arrived, and
    /// the client retries the same credentials with the code filled in.
    /// </para>
    /// </remarks>
    public string? TwoFactorCode { get; init; }
}
