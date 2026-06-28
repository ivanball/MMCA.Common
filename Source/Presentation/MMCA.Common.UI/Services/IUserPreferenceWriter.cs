namespace MMCA.Common.UI.Services;

/// <summary>
/// Persists the signed-in user's culture/theme preference to the backend so it follows them across
/// devices (ADR-027 / ADR-028). A <see langword="null"/> field means "leave unchanged". Implementations
/// must be best-effort and no-op for anonymous users — the cookie/localStorage remain the runtime channel,
/// so a failed or skipped persist never breaks the in-page switch.
/// </summary>
public interface IUserPreferenceWriter
{
    /// <summary>
    /// Persists the given preference for the current user. Either argument may be <see langword="null"/>
    /// to leave that preference unchanged.
    /// </summary>
    /// <param name="culture">The preferred culture (e.g. "es"), or <see langword="null"/> to leave unchanged.</param>
    /// <param name="theme">The preferred theme ("light"/"dark"), or <see langword="null"/> to leave unchanged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(string? culture, string? theme, CancellationToken cancellationToken = default);
}
