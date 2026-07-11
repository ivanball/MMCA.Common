using MMCA.Common.UI.Services.Capabilities;

namespace MMCA.Common.UI.Maui.Capabilities;

/// <summary>
/// Platform-direct <see cref="IBiometricAuthenticator"/> (ADR-042 Wave 4): Android uses the
/// AndroidX <c>BiometricPrompt</c> (biometric OR device credential), iOS/MacCatalyst use
/// <c>LAContext</c> with <c>DeviceOwnerAuthentication</c> (Face ID / Touch ID with passcode
/// fallback). Windows reports unavailable (the unpackaged WinUI head cannot present
/// <c>UserConsentVerifier</c>). Every negative outcome — cancel, lockout, error — returns
/// <see langword="false"/>; callers fall back to credential login, never a weaker path.
/// </summary>
public sealed class MauiBiometricAuthenticator : IBiometricAuthenticator
{
#if ANDROID
    private const int AllowedAuthenticators =
        AndroidX.Biometric.BiometricManager.Authenticators.BiometricWeak
        | AndroidX.Biometric.BiometricManager.Authenticators.DeviceCredential;

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var manager = AndroidX.Biometric.BiometricManager.From(Android.App.Application.Context);
        return Task.FromResult(
            manager.CanAuthenticate(AllowedAuthenticators) == AndroidX.Biometric.BiometricManager.BiometricSuccess);
    }

    /// <inheritdoc />
    public async Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (Platform.CurrentActivity is not AndroidX.Fragment.App.FragmentActivity activity)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var executor = AndroidX.Core.Content.ContextCompat.GetMainExecutor(activity);
            if (executor is null)
            {
                completion.TrySetResult(false);
                return;
            }

            var prompt = new AndroidX.Biometric.BiometricPrompt(activity, executor, new AuthenticationCallback(completion));
            var promptInfo = new AndroidX.Biometric.BiometricPrompt.PromptInfo.Builder()
                .SetTitle(reason)
                .SetAllowedAuthenticators(AllowedAuthenticators)
                .Build();
            prompt.Authenticate(promptInfo);
        });

        await using var registration = cancellationToken.Register(() => completion.TrySetResult(false));
        return await completion.Task;
    }

    private sealed class AuthenticationCallback(TaskCompletionSource<bool> completion)
        : AndroidX.Biometric.BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(AndroidX.Biometric.BiometricPrompt.AuthenticationResult result) =>
            completion.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence errString) =>
            completion.TrySetResult(false);

        // OnAuthenticationFailed is a single bad attempt; the prompt stays up, so don't complete.
    }
#elif IOS || MACCATALYST
    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        using var context = new LocalAuthentication.LAContext();
        return Task.FromResult(
            context.CanEvaluatePolicy(LocalAuthentication.LAPolicy.DeviceOwnerAuthentication, out _));
    }

    /// <inheritdoc />
    public async Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken = default)
    {
        using var context = new LocalAuthentication.LAContext();
        if (!context.CanEvaluatePolicy(LocalAuthentication.LAPolicy.DeviceOwnerAuthentication, out _))
        {
            return false;
        }

        var (success, _) = await context
            .EvaluatePolicyAsync(LocalAuthentication.LAPolicy.DeviceOwnerAuthentication, reason)
            .ConfigureAwait(false);
        return success;
    }
#else
    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <inheritdoc />
    public Task<bool> AuthenticateAsync(string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
#endif
}
