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

    /// <summary>
    /// Gets a human-readable name for the scope currently in force (the conference event's title, the
    /// tenant's name), or null when there is nothing to show. The send page uses it to caption which
    /// scope the notification will be tagged with, so an operator can see the auto-applied scope
    /// rather than infer it. The scope only decides which inbox view (for example which event's)
    /// lists the notification; it does not narrow delivery, which goes to every recipient the
    /// application's recipient provider returns, so the caption must not be read as the audience.
    /// <para>
    /// It is a default interface method returning null: an application that has no display name, and
    /// every existing implementation, keeps compiling untouched. The same never-throw, fail-closed
    /// contract as <see cref="GetCurrentScopeKeyAsync"/> applies, except that failing closed here
    /// means returning null: a missing caption hides information, while a wrong one would state the
    /// wrong audience.
    /// </para>
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The scope display name, or null when none is available.</returns>
    Task<string?> GetCurrentScopeDisplayNameAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}
