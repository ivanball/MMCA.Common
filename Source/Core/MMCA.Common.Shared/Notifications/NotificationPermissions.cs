namespace MMCA.Common.Shared.Notifications;

/// <summary>
/// Permission constants for the Notification module. A host grants these to the roles it wants to
/// hold them through <c>AddPermissions(...)</c>; the endpoints state the capability, never a role.
/// </summary>
public static class NotificationPermissions
{
    /// <summary>Sending push notifications and reading the send history.</summary>
    public const string Manage = "notifications:manage";
}
