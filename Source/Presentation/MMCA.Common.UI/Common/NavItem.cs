namespace MMCA.Common.UI.Common;

/// <summary>
/// Describes a sidebar navigation entry contributed by a UI module.
/// When <paramref name="RequiredRole"/> is set, the item is only rendered for users in that role.
/// When <paramref name="RequiredClaim"/> is set, the item is only rendered for users with that claim type.
/// <paramref name="Section"/> determines which sidebar group the item appears under.
/// <paramref name="Group"/> optionally nests the item inside a collapsible <c>MudNavGroup</c>.
/// <para>
/// Localization (ADR-027): when <paramref name="TitleResource"/> is set, <paramref name="Title"/> and
/// <paramref name="Group"/> are treated as resource KEYS resolved against that resource type at render
/// time (per-circuit, so the menu follows the active culture); when the key is missing, or
/// <paramref name="TitleResource"/> is <see langword="null"/>, the raw string renders as before, so
/// existing literal-titled items keep working unchanged.
/// </para>
/// </summary>
public record NavItem(string Title, string Href, string Icon, string? RequiredRole = null, string? RequiredClaim = null, NavSection Section = NavSection.General, string? Group = null, Type? TitleResource = null);
