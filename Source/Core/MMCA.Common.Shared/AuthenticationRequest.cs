namespace MMCA.Common.Shared;

/// <summary>
/// Device-aware authentication request used by mobile/MAUI clients. Captures device metadata
/// alongside credentials to support per-device session tracking and token management.
/// </summary>
/// <param name="DeviceId">Unique device identifier (platform-generated).</param>
/// <param name="Email">The user's email address.</param>
/// <param name="DeviceFormFactor">Device form factor (e.g. "Phone", "Tablet", "Desktop").</param>
/// <param name="DevicePlatform">Operating system platform (e.g. "Android", "iOS", "Windows").</param>
/// <param name="DeviceModel">Device hardware model identifier.</param>
/// <param name="DeviceManufacturer">Device manufacturer name.</param>
/// <param name="DeviceName">User-assigned device name.</param>
/// <param name="DeviceType">Device type classification.</param>
public readonly record struct AuthenticationRequest(
    string DeviceId,
    string Email,
    string DeviceFormFactor,
    string DevicePlatform,
    string DeviceModel,
    string DeviceManufacturer,
    string DeviceName,
    string DeviceType);
