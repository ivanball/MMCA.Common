namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Defines canonical role name constants shared across all layers and modules.
/// Use these constants instead of string literals when checking or assigning roles.
/// </summary>
/// <remarks>
/// Role names are stored as plain strings in the database and emitted as JWT claims.
/// These constants ensure consistent, typo-free role references throughout the codebase.
/// All role comparisons should be case-insensitive (see <c>ICurrentUserService.IsInRole</c>).
/// </remarks>
public static class RoleNames
{
    /// <summary>Organizer role — manages conference master data (ADC).</summary>
    public const string Organizer = "Organizer";

    /// <summary>Attendee role — default for new registrations (ADC).</summary>
    public const string Attendee = "Attendee";

    /// <summary>Administrator role — full access to admin features (Store).</summary>
    public const string Admin = "Admin";

    /// <summary>Customer role — standard customer access (Store).</summary>
    public const string Customer = "Customer";
}
