namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// No-op scope provider: always unscoped. Registered as the default by <c>AddNotificationUI</c>, so
/// an application that never scopes its notifications keeps exactly the behaviour it had before the
/// scope key existed. Apps that do scope register their own implementation, which wins over this one.
/// </summary>
public sealed class NullNotificationScopeProvider : INotificationScopeProvider
{
    /// <inheritdoc />
    public Task<string?> GetCurrentScopeKeyAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
