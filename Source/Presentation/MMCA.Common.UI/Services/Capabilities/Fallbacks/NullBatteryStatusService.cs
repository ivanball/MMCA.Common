namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>Default <see cref="IBatteryStatusService"/>: energy saver never reported active.</summary>
public sealed class NullBatteryStatusService : IBatteryStatusService
{
    /// <inheritdoc />
    public event EventHandler? EnergySaverChanged
    {
        add
        {
            // Never raised: no battery state on this host.
        }

        remove
        {
            // Never raised: no battery state on this host.
        }
    }

    /// <inheritdoc />
    public bool IsEnergySaverOn => false;
}
