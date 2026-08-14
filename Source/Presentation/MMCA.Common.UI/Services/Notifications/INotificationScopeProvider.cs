namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// Supplies the scope key the notification UI should send and read under. The framework has no idea
/// what an application's notifications are scoped by (a conference event, a tenant, a season), so an
/// app implements this and both notification HTTP services consume it, which is what keeps a send
/// and the reads that follow agreeing on one scope.
/// </summary>
/// <remarks>
/// Implementations must never throw: a scope is a view filter, not a security boundary, and the safe
/// direction on any failure is null (unscoped), which restores the pre-scope behaviour rather than
/// breaking the bell or the inbox.
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
