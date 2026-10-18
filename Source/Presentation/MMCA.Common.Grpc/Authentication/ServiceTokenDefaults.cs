namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// The names shared by both ends of a service-to-service call: the authentication scheme the
/// receiving service validates service tokens under, the authorization policy that admits either a
/// user or a service caller, and the claim that marks a token as a service token.
/// </summary>
public static class ServiceTokenDefaults
{
    /// <summary>
    /// The authentication scheme <c>AddServiceTokenAuthentication</c> registers. Never the default
    /// scheme: a service token only authenticates an endpoint that names this scheme, which in
    /// practice means an endpoint under <see cref="InternalCallerPolicy"/>.
    /// </summary>
    public const string AuthenticationScheme = "ServiceToken";

    /// <summary>
    /// The authorization policy for internal endpoints: an authenticated caller from either the
    /// user JwtBearer scheme or <see cref="AuthenticationScheme"/>.
    /// </summary>
    public const string InternalCallerPolicy = "InternalCaller";

    /// <summary>
    /// The claim carrying the calling service's name. A token without it is not a service token
    /// and is refused by the <see cref="AuthenticationScheme"/> scheme.
    /// </summary>
    public const string ServiceClaimType = "svc";
}
