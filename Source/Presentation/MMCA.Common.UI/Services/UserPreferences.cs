namespace MMCA.Common.UI.Services;

/// <summary>
/// A user's persisted UI preferences (ADR-027 / ADR-028). A <see langword="null"/> field means the user
/// has not chosen that preference and the request default / OS preference applies.
/// </summary>
/// <param name="Culture">The preferred culture (e.g. "es"), or <see langword="null"/>.</param>
/// <param name="Theme">The preferred theme ("light"/"dark"), or <see langword="null"/>.</param>
public sealed record UserPreferences(string? Culture, string? Theme);
