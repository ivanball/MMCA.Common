using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.UI.Services.Auth.TwoFactor;

/// <summary>
/// Client for the signed-in account's own two-factor authentication (<c>auth/two-factor</c>):
/// its status, enrollment, recovery codes and turning it off. Registered by <c>AddTwoFactorUI()</c>.
/// </summary>
/// <remarks>
/// A separate interface rather than new <c>IAuthUIService</c> members, so a consumer's own
/// <c>IAuthUIService</c> implementation keeps compiling. Every write is sent once and never retried:
/// enrollment mints a fresh secret per call, a code is accepted once per time step, and a regenerated
/// set replaces the previous one, so a blind retry could only spend a code twice or mint a second secret.
/// </remarks>
public interface ITwoFactorUIService
{
    /// <summary>Reads whether the account's second factor is on and how many recovery codes are left.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status, or the API's own refusal.</returns>
    Task<Result<TwoFactorStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts enrollment: the API mints a fresh secret and returns it in both forms an authenticator app
    /// consumes. The second factor is not on until <see cref="ConfirmEnrollmentAsync"/> succeeds.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The shared key and provisioning URI, or the API's own refusal.</returns>
    Task<Result<TwoFactorSetupResponse>> BeginEnrollmentAsync(CancellationToken cancellationToken = default);

    /// <summary>Confirms enrollment with a code from the newly keyed app, which turns the second factor on.</summary>
    /// <param name="code">The code from the authenticator app.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recovery codes, shown once, or the API's own refusal.</returns>
    Task<Result<TwoFactorRecoveryCodesResponse>> ConfirmEnrollmentAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Replaces every recovery code with a new set, proved with a live code.</summary>
    /// <param name="code">A code from the authenticator app, or a recovery code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new recovery codes, shown once, or the API's own refusal.</returns>
    Task<Result<TwoFactorRecoveryCodesResponse>> RegenerateRecoveryCodesAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Turns the second factor off, proved with a live code.</summary>
    /// <param name="code">A code from the authenticator app, or a recovery code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's own refusal.</returns>
    Task<Result> DisableAsync(string code, CancellationToken cancellationToken = default);
}
