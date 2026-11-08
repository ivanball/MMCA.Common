using MMCA.Common.Domain.Auth.MachineClients;

namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>
/// Persistence for <see cref="MachineClient"/> rows (ADR-140). The framework registers an EF Core
/// implementation over the Identity database; every caller is behind <c>MachineClients:Enabled</c>,
/// so it never queries a table the model does not carry.
/// </summary>
public interface IMachineClientStore
{
    /// <summary>Lists every client, revoked ones included, oldest first.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The clients.</returns>
    Task<IReadOnlyList<MachineClient>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds a client by id; the instance is tracked, so <see cref="SaveAsync"/> persists changes to it.</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The client, or <see langword="null"/>.</returns>
    Task<MachineClient?> FindAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>Adds a new client and saves.</summary>
    /// <param name="client">The client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the row is saved.</returns>
    Task AddAsync(MachineClient client, CancellationToken cancellationToken = default);

    /// <summary>Saves the changes made to a client returned by <see cref="FindAsync"/>.</summary>
    /// <param name="client">The changed client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the row is saved.</returns>
    Task SaveAsync(MachineClient client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a token issuance on a client returned by <see cref="FindAsync"/>
    /// (<see cref="MachineClient.RecordUse"/>) and saves it, best effort: a concurrent issuance that
    /// saved first wins, since the instant is informational and must never fail a token request.
    /// </summary>
    /// <param name="client">The client that just obtained a token.</param>
    /// <param name="usedOn">The UTC instant of the issuance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the row is saved or the conflict is dropped.</returns>
    Task RecordUseAsync(MachineClient client, DateTime usedOn, CancellationToken cancellationToken = default);
}
