using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.UI.Services.Legal;

/// <summary>
/// The signed-in user's Terms of Service acceptance calls behind <c>TermsAcceptanceGate</c>:
/// read the standing, and record an acceptance of the version the user was shown.
/// </summary>
/// <remarks>
/// Both calls go to <see cref="LegalAcceptanceRoutes.Path"/> with the caller's bearer token. Every
/// member returns a <see cref="Result"/> carrying the API's own errors (404 included, for a host that
/// serves no such endpoint); nothing throws for a server answer or a transport fault, so a caller can
/// treat any failure as "do not block".
/// </remarks>
public interface ILegalAcceptanceUIService
{
    /// <summary>Reads the signed-in user's standing via <c>GET Users/me/legal-acceptance</c>.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The standing, or the API's failure.</returns>
    Task<Result<LegalAcceptanceDTO>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Records an acceptance via <c>POST Users/me/legal-acceptance</c>.</summary>
    /// <param name="version">The version the user was shown and agreed to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The standing after the acceptance, or the API's failure.</returns>
    Task<Result<LegalAcceptanceDTO>> AcceptAsync(string version, CancellationToken cancellationToken = default);
}
