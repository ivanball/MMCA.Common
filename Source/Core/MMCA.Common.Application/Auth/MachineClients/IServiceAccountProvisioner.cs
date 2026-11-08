using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>
/// Creates and reads the service-account users behind machine clients (ADR-140). The consumer
/// implements it over its own <c>User</c> aggregate (Common has no user entity), the ADR-116
/// consumer-supplied-store shape; <c>MachineClients:Enabled</c> without a registration fails startup.
/// </summary>
/// <remarks>
/// A service account is a user whose <c>IAuthUser.IsServiceAccount</c> answers <see langword="true"/>:
/// no password, no external login, never a person. The framework refuses it on every interactive
/// sign-in path and as a view-as target, so the only way it obtains a token is its machine client.
/// </remarks>
public interface IServiceAccountProvisioner
{
    /// <summary>
    /// Creates a service-account user with the given role and no password, and returns its id.
    /// </summary>
    /// <param name="name">The display name (the machine client's name).</param>
    /// <param name="role">The role the account is created with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new account's id, or the app's own refusal (an unknown role, say).</returns>
    Task<Result<UserIdentifierType>> CreateAsync(string name, string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a service account, or returns <see langword="null"/> when no such account exists, it is
    /// deleted or locked, or it is not a service account (so a token is never minted for a person).
    /// </summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account's facts, or <see langword="null"/>.</returns>
    Task<ServiceAccount?> FindAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);
}
