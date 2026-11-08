using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Passkeys;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The passkey ceremonies and the management of an account's passkeys (ADR-139), registered by
/// <c>AddPasskeys</c>. The WebAuthn verification lives behind this interface in Infrastructure; the
/// controller base only gates and maps.
/// </summary>
public interface IPasskeyService
{
    /// <summary>Gets a value indicating whether passkeys are enabled on this host (<c>Passkeys:Enabled</c>).</summary>
    bool IsEnabled { get; }

    /// <summary>Starts a registration for a signed-in account: stores a single-use challenge and returns the options.</summary>
    /// <param name="userId">The account the passkey will sign in.</param>
    /// <param name="userName">The account name the authenticator shows (usually the email).</param>
    /// <param name="displayName">The friendly name the authenticator shows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow and the creation options.</returns>
    Task<Result<PasskeyOptionsResponse>> BeginRegistrationAsync(
        UserIdentifierType userId,
        string userName,
        string displayName,
        CancellationToken cancellationToken = default);

    /// <summary>Completes a registration: redeems the challenge, verifies the attestation and stores the passkey.</summary>
    /// <param name="userId">The signed-in account; must be the one the flow was issued to.</param>
    /// <param name="request">The flow and the attestation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored passkey, or the refusal.</returns>
    Task<Result<PasskeyResponse>> CompleteRegistrationAsync(
        UserIdentifierType userId,
        PasskeyRegisterRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the account's passkeys.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The passkeys, oldest first.</returns>
    Task<Result<IReadOnlyList<PasskeyResponse>>> ListAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);

    /// <summary>Renames one of the account's passkeys.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="id">The passkey row id.</param>
    /// <param name="name">The new label.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The renamed passkey, or the refusal.</returns>
    Task<Result<PasskeyResponse>> RenameAsync(
        UserIdentifierType userId,
        Guid id,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one of the account's passkeys. The last one goes only while the account still has a
    /// password or an external login (<see cref="IAuthenticationService.HasSignInMethodBesidesPasskeysAsync"/>).
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="id">The passkey row id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A success result, or the refusal.</returns>
    Task<Result> DeleteAsync(UserIdentifierType userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Starts an anonymous sign-in with a discoverable credential: stores a single-use challenge and returns the options.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow and the request options.</returns>
    Task<Result<PasskeyOptionsResponse>> BeginSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a sign-in: redeems the challenge, verifies the assertion against the stored public key
    /// and counter, and mints the token pair through <see cref="IAuthenticationService.SignInWithPasskeyAsync"/>.
    /// </summary>
    /// <param name="request">The flow and the assertion.</param>
    /// <param name="ipAddress">Optional client IP recorded on the session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token pair, or the refusal.</returns>
    Task<Result<AuthenticationResponse>> CompleteSignInAsync(
        PasskeySignInRequest request,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default);
}
