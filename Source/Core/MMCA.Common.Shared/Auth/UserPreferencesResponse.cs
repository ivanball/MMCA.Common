namespace MMCA.Common.Shared.Auth;

/// <summary>
/// The current user's stored UI preferences (ADR-027 / ADR-028). A <see langword="null"/> field means
/// the user has not chosen that preference.
/// </summary>
/// <param name="Culture">The preferred culture (e.g. "es"), or <see langword="null"/>.</param>
/// <param name="Theme">The preferred theme ("light"/"dark"), or <see langword="null"/>.</param>
public sealed record UserPreferencesResponse(string? Culture, string? Theme);
