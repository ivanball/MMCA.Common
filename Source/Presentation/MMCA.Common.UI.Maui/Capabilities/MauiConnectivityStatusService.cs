using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IConnectivityStatusService"/> over <c>Connectivity.Current</c>.
/// Captive-portal ("constrained") access counts as offline: the API gateway is unreachable
/// there, which is exactly what the offline banner should say. Singleton; subscribes to the
/// platform event for its lifetime.
/// </summary>
public sealed partial class MauiConnectivityStatusService : IConnectivityStatusService, IDisposable
{
    /// <summary>Initializes the service and starts observing platform connectivity changes.</summary>
    public MauiConnectivityStatusService() =>
        Connectivity.Current.ConnectivityChanged += OnPlatformConnectivityChanged;

    /// <inheritdoc />
    public event EventHandler? ConnectivityChanged;

    /// <inheritdoc />
    public bool IsOnline => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => Connectivity.Current.ConnectivityChanged -= OnPlatformConnectivityChanged;

    private void OnPlatformConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        ConnectivityChanged?.Invoke(this, EventArgs.Empty);
}
