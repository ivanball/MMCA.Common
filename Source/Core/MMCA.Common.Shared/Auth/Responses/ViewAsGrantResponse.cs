namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// The answer to a granted view-as request (ADR-137): a short-lived, read-only access token for the
/// viewed account, and nothing to renew it with.
/// </summary>
/// <remarks>
/// There is deliberately no refresh token: a view-as token is never renewed. On a host with the
/// same-origin API proxy the proxy moves <paramref name="AccessToken"/> into its overlay cookie and
/// hands the browser the claims-only copy, exactly as it does for a sign-in.
/// </remarks>
/// <param name="AccessToken">The signed view-as access token (it carries <c>act_sub</c> and <c>vag</c>).</param>
/// <param name="AccessTokenExpiry">The UTC instant the token and its grant expire.</param>
/// <param name="GrantId">The grant row the token was minted for.</param>
/// <param name="TargetUserId">The account being viewed.</param>
public sealed record ViewAsGrantResponse(
    string AccessToken,
    DateTime AccessTokenExpiry,
    Guid GrantId,
    UserIdentifierType TargetUserId);
