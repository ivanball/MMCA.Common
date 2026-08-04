namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Request payload to update the current user's UI preferences (ADR-027 / ADR-028). A <see langword="null"/>
/// field leaves that preference unchanged, so the app-bar switcher (culture only) and theme toggle
/// (theme only) can each persist their own field without clobbering the other.
/// </summary>
/// <param name="Culture">Preferred culture (e.g. "es"), or <see langword="null"/> to leave unchanged.</param>
/// <param name="Theme">Preferred theme ("light"/"dark"), or <see langword="null"/> to leave unchanged.</param>
public sealed record ChangePreferencesRequest(string? Culture, string? Theme);
