namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// No-op push registration (ADR-044): web heads receive real-time notifications over the
/// SignalR hub while the page is open and have no OS-level installation to manage.
/// </summary>
public sealed class NullPushRegistrationService : IPushRegistrationService
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<bool> RegisterAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task UnregisterAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
