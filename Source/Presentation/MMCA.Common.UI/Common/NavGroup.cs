namespace MMCA.Common.UI.Common;

/// <summary>
/// Describes how the shared nav menu renders the group whose <see cref="NavItem.Group"/> equals
/// <paramref name="Key"/>: its icon, whether it starts expanded, and its position among the units of
/// its section (each group, plus the section's ungrouped items, which count as order 0).
/// <para>
/// A module declares groups through <see cref="Interfaces.IUIModule.NavGroups"/>, by key, so it may
/// describe a group whose items another module contributes (for example a framework-owned entry an
/// app wants to place). When several modules describe the same key, the first registered wins. A
/// group nobody describes renders as it always has: the folder icon, collapsed, in the order its
/// first item appears.
/// </para>
/// <para>
/// The group's heading is still the <paramref name="Key"/> resolved against the first item's
/// <see cref="NavItem.TitleResource"/> (ADR-027), so a description never carries display text.
/// </para>
/// </summary>
/// <param name="Key">The <see cref="NavItem.Group"/> value this description applies to.</param>
/// <param name="Icon">The group's icon; <see langword="null"/> keeps the folder icon.</param>
/// <param name="Expanded">Whether the group starts expanded.</param>
/// <param name="Order">The group's position within its section; ties keep first-appearance order.</param>
public sealed record NavGroup(string Key, string? Icon = null, bool Expanded = false, int Order = 0);
