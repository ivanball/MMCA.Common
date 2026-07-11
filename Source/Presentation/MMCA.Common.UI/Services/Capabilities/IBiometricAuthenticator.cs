namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Prompts for platform biometric/local authentication (fingerprint, Face ID, Windows Hello)
/// to gate stored-token auto-login behind an opt-in app lock. Availability and outcome are
/// booleans by design: callers fall back to the normal credential login on any failure,
/// never to a weaker path.
/// </summary>
public interface IBiometricAuthenticator
{
    /// <summary>Whether biometric or device-credential authentication can be presented right now.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows the platform authentication prompt with the given localized <paramref name="reason"/>.
    /// Returns <see langword="true"/> only on positive verification; cancellation, lockout,
    /// and errors all return <see langword="false"/>.
    /// </summary>
    Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken = default);
}
