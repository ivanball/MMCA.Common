using System.Globalization;

namespace MMCA.Common.Application.Users.UseCases.TwoFactor;

/// <summary>
/// The failed-attempt counter key the post-sign-in second-factor handlers (confirm enrollment,
/// disable, regenerate recovery codes) share, so a wrong code on any of them counts against one
/// per-account budget (M228).
/// </summary>
/// <remarks>
/// Keyed per account and separate from the sign-in counter (keyed by email) and the change-password
/// counter (<c>password-change:{userId}</c>), the same way the change-password handler keeps its own:
/// a lockout here must not lock the owner out of signing in.
/// </remarks>
internal static class TwoFactorAttemptKey
{
    /// <summary>The counter key for one account.</summary>
    /// <param name="userId">The account presenting a second-factor code.</param>
    /// <returns>The counter key.</returns>
    internal static string For(UserIdentifierType userId) =>
        $"two-factor:{userId.ToString(CultureInfo.InvariantCulture)}";
}
