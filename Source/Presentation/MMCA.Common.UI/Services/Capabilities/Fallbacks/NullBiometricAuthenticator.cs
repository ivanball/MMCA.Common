namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IBiometricAuthenticator"/>: unavailable; hosts hide the app-lock toggle.</summary>
public sealed class NullBiometricAuthenticator : IBiometricAuthenticator
{
    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
