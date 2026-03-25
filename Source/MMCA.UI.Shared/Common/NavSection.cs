namespace MMCA.UI.Shared.Common;

/// <summary>
/// Identifies the sidebar section a <see cref="NavItem"/> belongs to.
/// Sections are rendered in enum declaration order.
/// </summary>
public enum NavSection
{
    /// <summary>Items visible to everyone (anonymous + authenticated), e.g., "Shop", "Events".</summary>
    General,

    /// <summary>Items for authenticated non-admin users, e.g., "My Orders", "My Profile".</summary>
    User,

    /// <summary>Items for administrators/organizers, e.g., "Products", "Categories".</summary>
    Admin
}
