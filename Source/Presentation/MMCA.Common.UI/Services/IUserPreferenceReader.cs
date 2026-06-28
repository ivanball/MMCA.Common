namespace MMCA.Common.UI.Services;

/// <summary>
/// Reads the signed-in user's persisted culture/theme preference from the backend (ADR-027 / ADR-028),
/// used at login to apply a returning user's choice across devices. Best-effort: returns an empty
/// <see cref="UserPreferences"/> (both <see langword="null"/>) for anonymous users or on any error, so a
/// failed read never blocks login.
/// </summary>
public interface IUserPreferenceReader
{
    /// <summary>Returns the current user's stored preferences, or empty when anonymous/unavailable.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<UserPreferences> GetAsync(CancellationToken cancellationToken = default);
}
