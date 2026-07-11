namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// Default <see cref="IConnectivityStatusService"/>: always online, never raises the event.
/// Correct for Blazor Server, where a lost connection tears down the circuit itself.
/// </summary>
public sealed class AlwaysOnlineConnectivityStatusService : IConnectivityStatusService
{
    /// <inheritdoc />
    public event EventHandler? ConnectivityChanged
    {
        add
        {
            // Never raised: connectivity is constant on this host.
        }

        remove
        {
            // Never raised: connectivity is constant on this host.
        }
    }

    /// <inheritdoc />
    public bool IsOnline => true;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
