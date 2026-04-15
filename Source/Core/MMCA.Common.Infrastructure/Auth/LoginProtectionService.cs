using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Implements brute-force and rate-limiting protection using <see cref="ICacheService"/>.
/// <list type="bullet">
///   <item><b>Login lockout</b>: after <see cref="LoginProtectionSettings.MaxFailedAttempts"/>
///     consecutive failures, applies exponential backoff lockout (1s, 2s, 4s, ...)
///     capped at <see cref="LoginProtectionSettings.MaxLockoutSeconds"/>.</item>
///   <item><b>Registration rate limit</b>: limits registrations per IP address within a
///     configurable time window.</item>
/// </list>
/// </summary>
public sealed class LoginProtectionService(
    ICacheService cacheService,
    IOptions<LoginProtectionSettings> settings) : ILoginProtectionService
{
    private readonly LoginProtectionSettings _settings = settings.Value;

    /// <inheritdoc />
    public async Task<Result> CheckLockoutAsync(string email, CancellationToken cancellationToken = default)
    {
        var lockoutKey = $"login:lockout:{email}";
        var isLockedOut = await cacheService.GetAsync<bool?>(lockoutKey, cancellationToken).ConfigureAwait(false) ?? false;

        return isLockedOut
            ? Result.Failure(Error.Unauthorized(
                "Auth.TooManyAttempts",
                "Too many failed login attempts. Please try again later.",
                nameof(CheckLockoutAsync)))
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task IncrementFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        var attemptsKey = $"login:attempts:{email}";
        var currentCount = await cacheService.GetAsync<int?>(attemptsKey, cancellationToken).ConfigureAwait(false) ?? 0;
        var newCount = currentCount + 1;
        await cacheService.SetAsync(
            attemptsKey,
            newCount,
            TimeSpan.FromMinutes(_settings.FailedAttemptWindowMinutes),
            cancellationToken).ConfigureAwait(false);

        if (newCount >= _settings.MaxFailedAttempts)
        {
            var excessAttempts = newCount - _settings.MaxFailedAttempts;
            var lockoutSeconds = Math.Min(1 << excessAttempts, _settings.MaxLockoutSeconds);
            var lockoutKey = $"login:lockout:{email}";
            await cacheService.SetAsync(lockoutKey, true, TimeSpan.FromSeconds(lockoutSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ResetFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        await cacheService.RemoveAsync($"login:attempts:{email}", cancellationToken).ConfigureAwait(false);
        await cacheService.RemoveAsync($"login:lockout:{email}", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> CheckRegistrationRateLimitAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return Result.Success();
        }

        var key = $"registration:ip:{ipAddress}";
        var registrationCount = await cacheService.GetAsync<int?>(key, cancellationToken).ConfigureAwait(false) ?? 0;

        return registrationCount >= _settings.MaxRegistrationsPerIpPerHour
            ? Result.Failure(Error.Unauthorized(
                "Auth.RegistrationRateLimitExceeded",
                "Too many registration attempts. Please try again later.",
                nameof(CheckRegistrationRateLimitAsync)))
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task IncrementRegistrationCountAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return;
        }

        var key = $"registration:ip:{ipAddress}";
        var currentCount = await cacheService.GetAsync<int?>(key, cancellationToken).ConfigureAwait(false) ?? 0;
        await cacheService.SetAsync(
            key,
            currentCount + 1,
            TimeSpan.FromMinutes(_settings.RegistrationRateLimitWindowMinutes),
            cancellationToken).ConfigureAwait(false);
    }
}
