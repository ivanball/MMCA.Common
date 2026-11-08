using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.Auth.MachineClients;

/// <summary>
/// The error answer of <c>POST auth/token</c>, in the RFC 6749 section 5.2 shape: one of
/// <see cref="InvalidClient"/>, <see cref="UnsupportedGrantType"/> or <see cref="InvalidRequest"/>.
/// No description is sent, so a refusal never says which half of the credential was wrong.
/// </summary>
/// <param name="Error">The RFC 6749 error code.</param>
public sealed record MachineTokenErrorResponse([property: JsonPropertyName("error")] string Error)
{
    /// <summary>The client could not be authenticated (unknown, revoked, expired, wrong secret or refused role).</summary>
    public const string InvalidClient = "invalid_client";

    /// <summary>The grant type is not <c>client_credentials</c>.</summary>
    public const string UnsupportedGrantType = "unsupported_grant_type";

    /// <summary>The request has no grant type at all.</summary>
    public const string InvalidRequest = "invalid_request";
}
