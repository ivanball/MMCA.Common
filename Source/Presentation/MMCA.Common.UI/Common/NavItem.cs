namespace MMCA.Common.UI.Common;

/// <summary>
/// Describes a sidebar navigation entry contributed by a UI module.
/// When <paramref name="RequiredRole"/> is set, the item is only rendered for users in that role.
/// When <paramref name="RequiredClaim"/> is set, the item is only rendered for users with that claim type.
/// When <paramref name="RequiredPermission"/> is set, the item is only rendered for users carrying a
/// <see cref="Shared.Auth.AuthClaimTypes.Permission"/> claim whose value matches it ordinally, which
/// is the capability gate the framework's own entries use: the item states what it needs, never which
/// role the host happens to grant it to.
/// <paramref name="Section"/> determines which sidebar group the item appears under.
/// <paramref name="Group"/> optionally nests the item inside a collapsible <c>MudNavGroup</c>.
/// <para>
/// Localization (ADR-027): <paramref name="Title"/> and <paramref name="Group"/> are resource KEYS
/// resolved against <paramref name="TitleResource"/> at render time (per-circuit, so the menu follows
/// the active culture). A key the resource type does not declare renders as the raw string, which is
/// what makes a not-yet-translated entry legible instead of blank.
/// </para>
/// </summary>
public record NavItem(string Title, string Href, string Icon, Type TitleResource, string? RequiredRole = null, string? RequiredClaim = null, string? RequiredPermission = null, NavSection Section = NavSection.General, string? Group = null);
