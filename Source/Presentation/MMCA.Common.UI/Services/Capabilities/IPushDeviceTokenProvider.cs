namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Supplies the platform push handle for THIS device (ADR-044). Apps plug in their credentialed
/// implementation (Firebase messaging token on Android, APNs device token on iOS); the default
/// returns <see langword="null"/>, which keeps the whole registration pipeline inert until real
/// push credentials exist. Implementations request notification permission as needed and never
/// throw.
/// </summary>
public interface IPushDeviceTokenProvider
{
    /// <summary>The current platform token, or <see langword="null"/> when unavailable.</summary>
    Task<PushDeviceToken?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>A platform push handle.</summary>
/// <param name="Platform">The wire platform value: <c>fcmv1</c> or <c>apns</c>.</param>
/// <param name="Token">The FCM registration token / APNs device token.</param>
public sealed record PushDeviceToken(string Platform, string Token);
