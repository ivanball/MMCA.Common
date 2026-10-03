using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for new user registration.
/// </summary>
/// <param name="Email">The email address for the new account.</param>
/// <param name="Password">The password for the new account.</param>
/// <param name="FirstName">The user's first name.</param>
/// <param name="LastName">The user's last name.</param>
/// <param name="Address">Optional postal address for the user.</param>
/// <param name="AcceptedTerms">
/// Whether the registrant ticked the "I agree to the Terms of Service" box. A plain flag rather than
/// a version string on purpose: the register page is anonymous and cannot read the current terms
/// version, so the server stamps the version it has configured. Ignored by a server that configures
/// no terms version; when one is configured and this is
/// <see langword="false"/>, the registration is refused.
/// </param>
public readonly record struct RegisterRequest(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    Address? Address = null,
    bool AcceptedTerms = false);
