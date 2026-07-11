namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// Default <see cref="IExternalAuthBroker"/>: no native broker, which keeps the shared Login
/// page on its anchor-href OAuth flow (the correct behavior for web heads).
/// </summary>
public sealed class UnavailableExternalAuthBroker : IExternalAuthBroker
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<bool> SignInAsync(string provider, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
