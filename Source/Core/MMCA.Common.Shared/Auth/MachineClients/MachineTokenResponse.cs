using System.Text.Json.Serialization;

namespace MMCA.Common.Shared.Auth.MachineClients;

/// <summary>
/// The successful answer of <c>POST auth/token</c> for the client credentials grant, in the RFC 6749
/// section 5.1 shape. There is deliberately no refresh token: a program holds its secret and asks
/// again (ADR-140).
/// </summary>
/// <param name="AccessToken">The signed access token.</param>
/// <param name="TokenType">Always <c>Bearer</c>.</param>
/// <param name="ExpiresIn">The token's lifetime in seconds.</param>
public sealed record MachineTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
