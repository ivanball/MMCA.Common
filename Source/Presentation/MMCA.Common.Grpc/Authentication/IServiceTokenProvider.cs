namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// Supplies the bearer token a service presents when it calls another service with no user token
/// to forward (a background processor, or an anonymous request that fans out internally).
/// <c>JwtForwardingClientInterceptor</c> consults it only after the user's own token was not found.
/// </summary>
public interface IServiceTokenProvider
{
    /// <summary>
    /// Returns a compact JWT naming this service, valid for at least the next 60 seconds. The value
    /// is the token only, without the <c>Bearer</c> prefix.
    /// </summary>
    /// <returns>The service token.</returns>
    string GetToken();
}
