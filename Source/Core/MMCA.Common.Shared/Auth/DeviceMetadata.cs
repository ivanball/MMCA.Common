namespace MMCA.Common.Shared.Auth;

/// <summary>
/// What a native client reports about the device it runs on, recorded on the caller's current refresh
/// session (one row per signed-in device) so a "signed-in devices" list and a sign-in history can name
/// the device in the words its owner knows it by. The same shape is the request body of
/// <c>PUT auth/sessions/current/device</c> and the <c>Device</c> member of the session responses.
/// <para>
/// Every member is optional and informational: nothing validates a session against it, and the server
/// truncates each value to its column width and stores a blank one as <see langword="null"/>. Browser
/// clients report nothing; their sessions are described from the user-agent instead.
/// </para>
/// </summary>
/// <param name="DeviceId">
/// A per-install identifier the client generates and keeps (a random GUID, since iOS exposes no stable
/// device id). It distinguishes two devices of the same model; it is not a hardware serial.
/// </param>
/// <param name="DeviceName">The name the owner gave the device (e.g. "Ana's phone"), when the platform exposes it.</param>
/// <param name="DeviceModel">The hardware model (e.g. "Pixel 8").</param>
/// <param name="DeviceManufacturer">The hardware manufacturer (e.g. "Google").</param>
/// <param name="DevicePlatform">The operating system (e.g. "Android", "iOS", "WinUI").</param>
/// <param name="DeviceFormFactor">The form factor (e.g. "Phone", "Tablet", "Desktop").</param>
/// <param name="DeviceType">Whether the device is "Physical" or "Virtual" (an emulator or simulator).</param>
public sealed record DeviceMetadata(
    string? DeviceId,
    string? DeviceName,
    string? DeviceModel,
    string? DeviceManufacturer,
    string? DevicePlatform,
    string? DeviceFormFactor,
    string? DeviceType);
