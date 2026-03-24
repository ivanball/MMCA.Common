using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload for new attendee registration.
/// </summary>
/// <param name="Email">The email address for the new account.</param>
/// <param name="Password">The password for the new account.</param>
/// <param name="FirstName">The attendee's first name.</param>
/// <param name="LastName">The attendee's last name.</param>
/// <param name="Address">Optional postal address for the attendee.</param>
public readonly record struct RegisterRequest(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    Address? Address = null);
