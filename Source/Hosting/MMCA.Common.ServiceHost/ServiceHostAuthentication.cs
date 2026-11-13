namespace MMCA.Common.ServiceHost;

/// <summary>
/// How a composed service host validates bearer tokens (<c>AddMmcaServiceHost</c>, rule 13).
/// </summary>
public enum ServiceHostAuthentication
{
    /// <summary>
    /// Validates tokens through JWKS discovery against <c>Authentication:JwtBearer:Authority</c>
    /// (<c>AddForwardedJwtBearer</c>): every service except the one that issues the tokens.
    /// </summary>
    ForwardedJwtBearer = 0,

    /// <summary>
    /// Validates tokens in process with the <c>Jwt</c> settings (<c>AddCommonAuthentication</c>): the
    /// Identity service, which issues them. No authority is read.
    /// </summary>
    TokenIssuer = 1,
}
