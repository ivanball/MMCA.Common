namespace MMCA.Common.UI.Common;

/// <summary>
/// Describes a sidebar navigation entry contributed by a UI module.
/// When <paramref name="RequiredRole"/> is set, the item is only rendered for users in that role.
/// When <paramref name="RequiredClaim"/> is set, the item is only rendered for users with that claim type.
/// <paramref name="Section"/> determines which sidebar group the item appears under.
/// <paramref name="Group"/> optionally nests the item inside a collapsible <c>MudNavGroup</c>.
/// </summary>
public record NavItem(string Title, string Href, string Icon, string? RequiredRole = null, string? RequiredClaim = null, NavSection Section = NavSection.General, string? Group = null);
