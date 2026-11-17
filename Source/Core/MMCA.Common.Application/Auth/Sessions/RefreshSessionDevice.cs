using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Auth.Sessions;

/// <summary>
/// Shapes the device columns of a <see cref="RefreshSession"/> into the <see cref="DeviceMetadata"/> the
/// session responses carry, shared by the device list, the sign-in history and the administrator's view
/// so the three cannot drift.
/// </summary>
internal static class RefreshSessionDevice
{
    private static readonly DeviceMetadata Empty = new(null, null, null, null, null, null, null);

    /// <summary>
    /// The device metadata recorded on the session, or null when nothing was recorded (every browser
    /// session, and every row anonymized for an erasure), so a response says "no device" rather than
    /// carrying an object of seven nulls.
    /// </summary>
    /// <param name="session">The session row.</param>
    /// <returns>The recorded metadata, or null.</returns>
    public static DeviceMetadata? ToDeviceMetadata(this RefreshSession session)
    {
        var device = new DeviceMetadata(
            session.DeviceId,
            session.DeviceName,
            session.DeviceModel,
            session.DeviceManufacturer,
            session.DevicePlatform,
            session.DeviceFormFactor,
            session.DeviceType);

        return device == Empty ? null : device;
    }
}
