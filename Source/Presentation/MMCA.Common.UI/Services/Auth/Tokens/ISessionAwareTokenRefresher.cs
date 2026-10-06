namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// Optional capability of an <see cref="ITokenRefresher"/>: reports WHY no token was acquired.
/// <see cref="ITokenRefresher.AcquireAccessTokenAsync"/> answers <see langword="null"/> both for
/// "there is no session" and for a transient failure; token storage that remembers a "no session"
/// answer for a while (the anonymous grace of <see cref="WasmTokenStorageService"/> and the Blazor
/// Server storage) must start that grace only on the definitive kind, or one 429 or dropped
/// connection would sign a signed-in user out for the grace period. A refresher that does not
/// implement this is treated as before: its <see langword="null"/> is read as "no session".
/// </summary>
public interface ISessionAwareTokenRefresher : ITokenRefresher
{
    /// <summary>Acquires an access token and reports the outcome.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The acquired token, a definitive "no session", or a transient failure.</returns>
    Task<TokenAcquisition> TryAcquireAccessTokenAsync(CancellationToken cancellationToken = default);
}
