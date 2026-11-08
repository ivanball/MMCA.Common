using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;

namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>
/// Machine clients (ADR-140): the client credentials grant and the administration of the clients
/// behind it. Registered by <c>AddMachineClients</c>; the controller bases resolve it per action and
/// answer 404 while <see cref="IsEnabled"/> is false.
/// </summary>
public interface IMachineClientService
{
    /// <summary>Gets a value indicating whether <c>MachineClients:Enabled</c> is true on this host.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Runs the client credentials grant: authenticates the client by id and secret and mints a
    /// short-lived access token for its service account. No refresh token and no session are issued.
    /// </summary>
    /// <param name="grantType">The presented <c>grant_type</c>; only <c>client_credentials</c> is accepted.</param>
    /// <param name="clientId">The presented <c>client_id</c>.</param>
    /// <param name="clientSecret">The presented <c>client_secret</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The token, or <c>MachineClients.InvalidClient</c> (any authentication failure, deliberately
    /// indistinguishable), <c>MachineClients.UnsupportedGrantType</c> or <c>MachineClients.InvalidRequest</c>.
    /// </returns>
    Task<Result<MachineTokenResponse>> IssueTokenAsync(
        string? grantType,
        string? clientId,
        string? clientSecret,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every client, revoked ones included.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The clients.</returns>
    Task<Result<IReadOnlyList<MachineClientResponse>>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a client: provisions its service account with the requested role, then stores the client
    /// with a new primary secret, which is returned once.
    /// </summary>
    /// <param name="request">The name, role and optional secret expiry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client and its secret, or a refusal (<c>MachineClients.RoleNotAllowed</c> among others).</returns>
    Task<Result<MachineClientSecretResponse>> CreateAsync(CreateMachineClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a secondary secret, returned once; both secrets work until it is promoted.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client and the new secret, or a refusal.</returns>
    Task<Result<MachineClientSecretResponse>> RotateAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>Makes the secondary secret the primary one; the old primary stops working at once.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client, or a refusal.</returns>
    Task<Result<MachineClientResponse>> PromoteAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a client: its secrets stop working, and the soft-deleted marker is written for its
    /// service account so every service rejects the tokens it already holds.
    /// </summary>
    /// <param name="clientId">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client, or a refusal.</returns>
    Task<Result<MachineClientResponse>> RevokeAsync(Guid clientId, CancellationToken cancellationToken = default);
}
