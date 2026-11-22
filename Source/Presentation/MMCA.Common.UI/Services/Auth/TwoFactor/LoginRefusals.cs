using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Services.Auth.TwoFactor;

/// <summary>What a refused password sign-in asks of the sign-in page.</summary>
internal enum LoginRefusal
{
    /// <summary>Any refusal without a step of its own: show the message.</summary>
    Other,

    /// <summary>The password was proved and the account has a second factor: ask for the code.</summary>
    SecondFactorRequired,

    /// <summary>The code did not verify: keep the code step and ask again.</summary>
    SecondFactorInvalid,

    /// <summary>The address is not confirmed yet: offer to send the confirmation link again.</summary>
    EmailNotConfirmed,
}

/// <summary>
/// The refusal-code classification and the notice mapping the shared sign-in page and the two-factor
/// account page share, kept out of their code-behind files. Matched on the error code, never on the
/// wording, which is localized and may change.
/// </summary>
internal static class LoginRefusals
{
    /// <summary>The <c>reason</c> the two-factor page sends after a successful enrollment.</summary>
    internal const string TwoFactorEnabledReason = "two-factor-enabled";

    /// <summary>The <c>reason</c> the two-factor page sends when the session must be replaced by one with a code.</summary>
    internal const string TwoFactorRequiredReason = "two-factor-required";

    /// <summary>
    /// The code the two-factor endpoints refuse enrollment with when the session did not sign in
    /// recently. Spelled here because the UI references the shared contracts only.
    /// </summary>
    internal const string RecentSignInRequiredCode = "Authentication.TwoFactorRecentSignInRequired";

    /// <summary>Classifies a refused sign-in by its single error code.</summary>
    /// <param name="result">The failed sign-in result.</param>
    /// <returns>The step the page should take.</returns>
    internal static LoginRefusal Classify(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Errors is not [var error])
        {
            return LoginRefusal.Other;
        }

        return error.Code switch
        {
            AuthErrorCodes.TwoFactorRequired => LoginRefusal.SecondFactorRequired,
            AuthErrorCodes.TwoFactorInvalid => LoginRefusal.SecondFactorInvalid,
            AuthErrorCodes.EmailNotConfirmed => LoginRefusal.EmailNotConfirmed,
            _ => LoginRefusal.Other,
        };
    }

    /// <summary>Whether <paramref name="result"/> carries the given error code.</summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="code">The code to look for.</param>
    /// <returns><see langword="true"/> when any error carries <paramref name="code"/>.</returns>
    internal static bool HasCode(Result result, string code) =>
        result.Errors.Any(error => string.Equals(error.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// The resource key of the notice the sign-in page shows for a <c>reason</c> query value, or
    /// <see langword="null"/> for any value it does not know (which is ignored).
    /// </summary>
    /// <param name="reason">The <c>reason</c> query value.</param>
    /// <returns>The notice key, or <see langword="null"/>.</returns>
    internal static string? ReasonNoticeKey(string? reason) => reason switch
    {
        TwoFactorEnabledReason => "Auth.Login.TwoFactorEnabledNotice",
        TwoFactorRequiredReason => "Auth.Login.TwoFactorRequiredNotice",
        _ => null,
    };

    /// <summary>
    /// Words for a refusal: the resource named by the single error code when one exists (the API's
    /// message is an English sentence), otherwise the API's message localized when it can be, otherwise
    /// the text of <paramref name="fallbackKey"/>.
    /// </summary>
    /// <param name="result">The failed result.</param>
    /// <param name="localizer">The shared localizer.</param>
    /// <param name="fallbackKey">The resource key used when nothing else is renderable.</param>
    /// <returns>The message.</returns>
    internal static string Describe(Result result, IStringLocalizer localizer, string fallbackKey)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localizer);

        if (result.Errors is [var error] && !string.IsNullOrEmpty(error.Code))
        {
            var byCode = localizer[error.Code];
            if (!byCode.ResourceNotFound)
            {
                return byCode.Value;
            }
        }

        return result.LocalizedErrorMessage(localizer) ?? localizer[fallbackKey].Value;
    }
}
