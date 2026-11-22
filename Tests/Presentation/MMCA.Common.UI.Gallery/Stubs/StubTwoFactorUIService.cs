using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Auth.TwoFactor;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Canned <see cref="ITwoFactorUIService"/> for the gallery, so the real <c>/profile/two-factor</c> page
/// walks every state by clicks with no backend: it starts off, "Set up" returns a fixed (fake) secret,
/// any code but <see cref="WrongCode"/> confirms and returns ten recovery codes, after which the page
/// reports the second factor on; "Generate new recovery codes" returns a fresh fixed set and "Turn off"
/// returns it to off. <see cref="WrongCode"/> is refused as <c>Authentication.TwoFactorInvalid</c>
/// everywhere, for the inline refusal state. One instance per circuit, so each E2E page starts off.
/// <para>
/// Confirming enrollment signs out and reloads, which starts a new circuit, so the states that follow
/// it are also reachable directly through a gallery-only query value the page itself ignores:
/// <c>/profile/two-factor?gallery-state=on</c> starts with the second factor on (add <c>required=true</c>
/// for the sign-in-again notice, since the gallery user carries no second factor), and
/// <c>?gallery-state=fail</c> makes the status read fail (the load-failure state with its retry).
/// </para>
/// </summary>
/// <param name="navigation">Read once, on the first status read, for the gallery-only starting state.</param>
internal sealed class StubTwoFactorUIService(NavigationManager navigation) : ITwoFactorUIService
{
    /// <summary>The code every action refuses.</summary>
    public const string WrongCode = "000000";

    /// <summary>The gallery-only query parameter that picks the starting state.</summary>
    public const string StateQueryName = "gallery-state";

    // Not a real secret: a fixed Base32 string that only keys the gallery's QR code and text key.
    private static readonly TwoFactorSetupResponse Setup = new(
        "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
        "otpauth://totp/Gallery:gallery-user%40gallery.test?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Gallery&digits=6&period=30");

    private static readonly IReadOnlyList<string> FirstCodes =
        ["AAAA-1111", "BBBB-2222", "CCCC-3333", "DDDD-4444", "EEEE-5555", "FFFF-6666", "GGGG-7777", "HHHH-8888", "JJJJ-9999", "KKKK-0000"];

    private static readonly IReadOnlyList<string> RegeneratedCodes =
        ["LLLL-1111", "MMMM-2222", "NNNN-3333", "PPPP-4444", "QQQQ-5555", "RRRR-6666", "SSSS-7777", "TTTT-8888", "UUUU-9999", "VVVV-0000"];

    private static readonly Error InvalidCode = Error.Unauthorized(
        AuthErrorCodes.TwoFactorInvalid,
        "The two-factor code is invalid.");

    private static readonly Error StatusUnavailable = Error.Failure(
        "Gallery.TwoFactorStatusUnavailable",
        "The two-factor status could not be read.");

    private bool _enabled;
    private int _remaining;
    private bool _started;
    private bool _failStatus;

    public Task<Result<TwoFactorStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ApplyStartingState();
        if (_failStatus)
        {
            // Fails once: the retry button then loads the status.
            _failStatus = false;
            return Task.FromResult(Result.Failure<TwoFactorStatusResponse>(StatusUnavailable));
        }

        return Task.FromResult(Result.Success(new TwoFactorStatusResponse(_enabled, _enabled ? _remaining : 0)));
    }

    public Task<Result<TwoFactorSetupResponse>> BeginEnrollmentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(Setup));

    public Task<Result<TwoFactorRecoveryCodesResponse>> ConfirmEnrollmentAsync(string code, CancellationToken cancellationToken = default)
    {
        if (IsWrong(code))
        {
            return Task.FromResult(Result.Failure<TwoFactorRecoveryCodesResponse>(InvalidCode));
        }

        _enabled = true;
        _remaining = FirstCodes.Count;
        return Task.FromResult(Result.Success(new TwoFactorRecoveryCodesResponse(FirstCodes)));
    }

    public Task<Result<TwoFactorRecoveryCodesResponse>> RegenerateRecoveryCodesAsync(string code, CancellationToken cancellationToken = default)
    {
        if (IsWrong(code))
        {
            return Task.FromResult(Result.Failure<TwoFactorRecoveryCodesResponse>(InvalidCode));
        }

        _remaining = RegeneratedCodes.Count;
        return Task.FromResult(Result.Success(new TwoFactorRecoveryCodesResponse(RegeneratedCodes)));
    }

    public Task<Result> DisableAsync(string code, CancellationToken cancellationToken = default)
    {
        if (IsWrong(code))
        {
            return Task.FromResult(Result.Failure(InvalidCode));
        }

        _enabled = false;
        _remaining = 0;
        return Task.FromResult(Result.Success());
    }

    private void ApplyStartingState()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var query = QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query);
        if (!query.TryGetValue(StateQueryName, out var state))
        {
            return;
        }

        if (string.Equals(state, "on", StringComparison.Ordinal))
        {
            _enabled = true;
            _remaining = 7;
        }
        else if (string.Equals(state, "fail", StringComparison.Ordinal))
        {
            _failStatus = true;
        }
    }

    private static bool IsWrong(string code) => string.Equals(code?.Trim(), WrongCode, StringComparison.Ordinal);
}
