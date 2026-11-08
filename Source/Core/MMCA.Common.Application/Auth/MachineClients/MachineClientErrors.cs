using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>The errors the machine-client workflow returns (ADR-140).</summary>
public static class MachineClientErrors
{
    /// <summary>The code of <see cref="InvalidClient"/>, which the token endpoint answers as <c>invalid_client</c>.</summary>
    public const string InvalidClientCode = "MachineClients.InvalidClient";

    /// <summary>The code of <see cref="UnsupportedGrantType"/>, answered as <c>unsupported_grant_type</c>.</summary>
    public const string UnsupportedGrantTypeCode = "MachineClients.UnsupportedGrantType";

    /// <summary>The code of <see cref="InvalidRequest"/>, answered as <c>invalid_request</c>.</summary>
    public const string InvalidRequestCode = "MachineClients.InvalidRequest";

    /// <summary>The code of <see cref="RoleNotAllowed"/>.</summary>
    public const string RoleNotAllowedCode = "MachineClients.RoleNotAllowed";

    /// <summary>
    /// The client could not be authenticated: unknown id, wrong secret, revoked, expired, missing or
    /// non-service account, or a role a client may not hold. One code for all of them, so a refusal
    /// never tells a caller which part was wrong.
    /// </summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error InvalidClient(string source) =>
        Error.Unauthorized(InvalidClientCode, "The client could not be authenticated.", source);

    /// <summary>The grant type is not <c>client_credentials</c>.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error UnsupportedGrantType(string source) =>
        Error.Validation(UnsupportedGrantTypeCode, "Only the client_credentials grant type is supported.", source);

    /// <summary>The request carries no grant type.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error InvalidRequest(string source) =>
        Error.Validation(InvalidRequestCode, "The token request must name a grant_type.", source);

    /// <summary>
    /// The role grants impersonation (<c>users:view-as</c>) or user management (<c>users:manage</c>),
    /// which no machine client may hold.
    /// </summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error RoleNotAllowed(string source) =>
        Error.Validation(RoleNotAllowedCode, "A role that grants viewing as users or managing users cannot back a machine client.", source, "role");

    /// <summary>A role is required to create a client.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error RoleRequired(string source) =>
        Error.Validation("MachineClients.RoleRequired", "A machine client requires a role.", source, "role");

    /// <summary>No client with that id exists.</summary>
    /// <param name="source">The member that refused.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string source) =>
        Error.NotFoundError("MachineClients.NotFound", "No such machine client.", source);
}
