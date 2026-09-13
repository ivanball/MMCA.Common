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
public readonly record struct RegisterRequest(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    Address? Address = null);
