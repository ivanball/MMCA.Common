using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// The client of the API's passkey endpoints (<c>auth/passkeys</c>, ADR-139). A separate interface
/// rather than new <see cref="IAuthUIService"/> members, so a consumer's own
/// <see cref="IAuthUIService"/> implementation keeps compiling (the <see cref="ISignInLinkUIService"/>
/// precedent). The framework's <see cref="AuthUIService"/> implements it, so a passkey sign-in stores
/// its tokens through the same path a password sign-in does.
/// </summary>
public interface IPasskeyUIService
{
    /// <summary>Starts registering a passkey for the signed-in user (<c>POST auth/passkeys/register/options</c>).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow and the creation options, or the refusal.</returns>
    Task<Result<PasskeyOptionsResponse>> BeginRegistrationAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes a registration with the browser's attestation (<c>POST auth/passkeys/register</c>).</summary>
    /// <param name="request">The flow, the attestation and the name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored passkey, or the refusal.</returns>
    Task<Result<PasskeyResponse>> CompleteRegistrationAsync(PasskeyRegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists the signed-in user's passkeys (<c>GET auth/passkeys</c>).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The passkeys, or the refusal.</returns>
    Task<Result<IReadOnlyList<PasskeyResponse>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames one of the signed-in user's passkeys (<c>PATCH auth/passkeys/{id}</c>).</summary>
    /// <param name="id">The passkey row id.</param>
    /// <param name="name">The new label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The renamed passkey, or the refusal.</returns>
    Task<Result<PasskeyResponse>> RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes one of the signed-in user's passkeys (<c>DELETE auth/passkeys/{id}</c>).</summary>
    /// <param name="id">The passkey row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the refusal (the last passkey of a password-less account).</returns>
    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Starts an anonymous passkey sign-in (<c>POST auth/passkeys/signin/options</c>).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow and the request options, or the refusal.</returns>
    Task<Result<PasskeyOptionsResponse>> BeginSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a passkey sign-in with the browser's assertion (<c>POST auth/passkeys/signin</c>),
    /// stores the token pair and notifies the auth state, like a password sign-in.
    /// </summary>
    /// <param name="request">The flow and the assertion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token pair, or the refusal.</returns>
    Task<Result<AuthenticationResponse>> SignInAsync(PasskeySignInRequest request, CancellationToken cancellationToken = default);
}
