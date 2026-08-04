using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The stored UI-preference surface an Identity module's <c>User</c> aggregate exposes to the shared
/// preference read/write workflows (ADR-027 culture, ADR-028 theme). A <see langword="null"/> value
/// means the user has not chosen that preference.
/// </summary>
public interface IUserPreferences
{
    /// <summary>The preferred culture (e.g. "es"), or <see langword="null"/> when unset.</summary>
    string? PreferredCulture { get; }

    /// <summary>The preferred theme ("light"/"dark"), or <see langword="null"/> when unset.</summary>
    string? PreferredTheme { get; }

    /// <summary>
    /// Replaces both preferences. The shared workflow always passes the stored value for a field the
    /// request left <see langword="null"/>, so writing one preference never clears the other.
    /// </summary>
    /// <param name="preferredCulture">The culture to store, or <see langword="null"/> to clear it.</param>
    /// <param name="preferredTheme">The theme to store, or <see langword="null"/> to clear it.</param>
    /// <returns>A success result, or the aggregate's invariant failure.</returns>
    Result UpdatePreferences(string? preferredCulture, string? preferredTheme);
}
