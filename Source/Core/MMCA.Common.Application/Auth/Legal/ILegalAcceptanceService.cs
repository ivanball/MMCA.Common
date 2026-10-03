using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Legal;

namespace MMCA.Common.Application.Auth.Legal;

/// <summary>
/// Reads and records a user's Terms of Service acceptance. The consumer implements it over its own
/// <c>User</c> aggregate (typically one implementing <c>ILegalAcceptingUser</c>), and
/// <c>LegalAcceptanceControllerBase</c> serves it.
/// </summary>
/// <remarks>
/// The version rules are not the implementation's job. The controller base resolves the configured
/// current version, refuses an acceptance of any other version
/// (<see cref="LegalAcceptancePolicy.EnsureAcceptsCurrentVersion"/>) before this service is called,
/// and re-derives <see cref="LegalAcceptanceDTO.CurrentVersion"/> and
/// <see cref="LegalAcceptanceDTO.IsCurrent"/> on the way out
/// (<see cref="LegalAcceptancePolicy.Normalize"/>). An implementation only has to load and save the
/// accepted version and instant; building its answer with <see cref="LegalAcceptanceDTO.Evaluate"/>
/// keeps it self-consistent for any other caller.
/// </remarks>
public interface ILegalAcceptanceService
{
    /// <summary>Reads the signed-in user's acceptance.</summary>
    /// <param name="currentUserId">The authenticated caller.</param>
    /// <param name="currentVersion">The configured current version (never null when called by the controller base).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's standing, or a failure such as NotFound.</returns>
    Task<Result<LegalAcceptanceDTO>> GetForCurrentUserAsync(
        UserIdentifierType currentUserId,
        string currentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the signed-in user accepted <paramref name="version"/>, stamped with the
    /// implementation's own clock.
    /// </summary>
    /// <param name="currentUserId">The authenticated caller.</param>
    /// <param name="version">The version accepted, already checked to be the configured current one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's standing after the acceptance, or a failure.</returns>
    Task<Result<LegalAcceptanceDTO>> AcceptForCurrentUserAsync(
        UserIdentifierType currentUserId,
        string version,
        CancellationToken cancellationToken = default);
}
