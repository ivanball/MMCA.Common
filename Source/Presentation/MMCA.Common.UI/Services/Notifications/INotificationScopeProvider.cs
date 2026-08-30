namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// Supplies the scope key the notification UI should send and read under. The framework has no idea
/// what an application's notifications are scoped by (a conference event, a tenant, a season), so an
/// app implements this and both notification HTTP services consume it, which is what keeps a send
/// and the reads that follow agreeing on one scope.
/// </summary>
/// <remarks>
/// Implementations must never throw. In an application whose notifications are all scoped, fail
/// closed: return the last known scope key (or fail the operation) rather than null, because
/// degrading to null silently widens the view to every notification. Return null only when the
/// application genuinely runs unscoped.
/// </remarks>
public interface INotificationScopeProvider
{
    /// <summary>
    /// Gets the scope key currently in force, or null when the application is unscoped.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The scope key (for example <c>"event:2"</c>), or null for no scoping.</returns>
    Task<string?> GetCurrentScopeKeyAsync(CancellationToken ct = default);
}
