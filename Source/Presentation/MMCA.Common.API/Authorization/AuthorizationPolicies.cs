using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Defines constant policy names for use in <c>[Authorize(Policy = ...)]</c> attributes.
/// These names must match the policies registered in <see cref="AuthorizationExtensions"/>.
/// Constants are used (rather than an enum) because attribute arguments require compile-time constants.
/// </summary>
[SuppressMessage("SonarAnalyzer.CSharp", "S2339", Justification = "Constants are required for use in attribute arguments.")]
public static class AuthorizationPolicies
{
    /// <summary>Requires the "Organizer" role claim.</summary>
    public const string RequireOrganizer = nameof(RequireOrganizer);

    /// <summary>Requires the "Attendee" role claim.</summary>
    public const string RequireAttendee = nameof(RequireAttendee);

    /// <summary>Requires the "Admin" role claim.</summary>
    public const string RequireAdmin = nameof(RequireAdmin);

    /// <summary>Requires any authenticated user, regardless of role.</summary>
    public const string RequireAuthenticated = nameof(RequireAuthenticated);
}
