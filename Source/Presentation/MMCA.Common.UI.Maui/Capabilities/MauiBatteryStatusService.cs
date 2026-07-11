using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// MAUI <see cref="IBatteryStatusService"/> over <c>Battery.Default</c>. Singleton;
/// observes energy-saver transitions for its lifetime so live features can throttle.
/// </summary>
public sealed partial class MauiBatteryStatusService : IBatteryStatusService, IDisposable
{
    /// <summary>Initializes the service and starts observing energy-saver changes.</summary>
    public MauiBatteryStatusService() =>
        Battery.Default.EnergySaverStatusChanged += OnEnergySaverStatusChanged;

    /// <inheritdoc />
    public event EventHandler? EnergySaverChanged;

    /// <inheritdoc />
    public bool IsEnergySaverOn => Battery.Default.EnergySaverStatus == EnergySaverStatus.On;

    /// <inheritdoc />
    public void Dispose() => Battery.Default.EnergySaverStatusChanged -= OnEnergySaverStatusChanged;

    private void OnEnergySaverStatusChanged(object? sender, EnergySaverStatusChangedEventArgs e) =>
        EnergySaverChanged?.Invoke(this, EventArgs.Empty);
}
