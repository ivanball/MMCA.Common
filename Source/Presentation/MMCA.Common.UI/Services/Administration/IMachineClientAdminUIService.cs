using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// Client for the app's <c>Admin/MachineClients</c> endpoints (ADR-140), the
/// <c>MachineClientsControllerBase</c> surface. Registered by <c>AddMachineClientAdministrationUI</c>.
/// </summary>
public interface IMachineClientAdminUIService
{
    /// <summary>Lists every machine client, revoked ones included.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The clients, or the API's refusal.</returns>
    Task<Result<IReadOnlyList<MachineClientResponse>>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a client; the result carries its secret, once.</summary>
    /// <param name="request">The name, role and optional secret expiry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client and its secret, or the API's refusal.</returns>
    Task<Result<MachineClientSecretResponse>> CreateAsync(CreateMachineClientRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues a secondary secret, returned once.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client and the new secret, or the API's refusal.</returns>
    Task<Result<MachineClientSecretResponse>> RotateAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>Makes the secondary secret the primary one.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client, or the API's refusal.</returns>
    Task<Result<MachineClientResponse>> PromoteAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>Revokes a client.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client, or the API's refusal.</returns>
    Task<Result<MachineClientResponse>> RevokeAsync(Guid clientId, CancellationToken cancellationToken = default);
}
