namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Exposes the platform energy-saver state so live features (SignalR channel auto-join)
/// can throttle themselves on a draining battery. Web/null fallbacks always report
/// <see langword="false"/> and never raise the event.
/// </summary>
public interface IBatteryStatusService
{
    /// <summary>Raised after <see cref="IsEnergySaverOn"/> changes. Handlers read the new value from the property.</summary>
    event EventHandler? EnergySaverChanged;

    /// <summary>Whether the OS energy saver / low power mode is currently active.</summary>
    bool IsEnergySaverOn { get; }
}
