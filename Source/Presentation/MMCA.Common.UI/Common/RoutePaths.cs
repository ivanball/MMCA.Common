namespace MMCA.Common.UI.Common;

/// <summary>
/// Centralized route path constants shared across all UI modules and hosts.
/// Module-specific paths live in their own <c>*RoutePaths</c> classes.
/// </summary>
public static class RoutePaths
{
    public static readonly string Home = "/";

    /// <summary>
    /// The signed-in devices page (<c>MMCA.Common.UI.Pages.Auth.Sessions</c>): the user's live
    /// refresh sessions, with per-device and account-wide sign-out. Framework-owned and reachable
    /// from the shared nav menu's authenticated section, so an app gets it without routing work.
    /// </summary>
    public static readonly string Sessions = "/profile/sessions";
}
