using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects;

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

    /// <summary>
    /// Normalizes the supplied address the same way <see cref="Email"/> does before it is used in a
    /// counter key. Without this the keys are built from raw request input while the user lookup
    /// runs against the normalized value object, so <c>User@x.com</c>, <c>user@x.com</c> and
    /// <c>" user@x.com "</c> resolve to one account but get independent attempt counters and
    /// lockouts: an attacker defeats the ADR-029 backoff just by varying capitalization.
    /// A malformed address (which never matches a user, but still increments a counter) falls back
    /// to the same trim-and-lowercase shape so its attempts collapse onto one key too.
    /// </summary>
    private static string NormalizeIdentity(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        var result = Email.Create(email);
#pragma warning disable CA1308 // Matches Email's own RFC 5321 lowercase normalization.
        return result.IsSuccess ? result.Value!.Value : email.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static string LockoutKey(string email) => $"login:lockout:{NormalizeIdentity(email)}";

    private static string AttemptsKey(string email) => $"login:attempts:{NormalizeIdentity(email)}";

    /// <inheritdoc />
    public async Task<Result> CheckLockoutAsync(string email, CancellationToken cancellationToken = default)
    {
        var lockoutKey = LockoutKey(email);
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
        // Atomic: a read-modify-write lets parallel attempts overwrite each other's increments, so
        // a burst of concurrent guesses could stay below MaxFailedAttempts indefinitely.
        var newCount = await cacheService.IncrementAsync(
            AttemptsKey(email),
            TimeSpan.FromMinutes(_settings.FailedAttemptWindowMinutes),
            cancellationToken).ConfigureAwait(false);

        if (newCount >= _settings.MaxFailedAttempts)
        {
            var excessAttempts = (int)Math.Min(newCount - _settings.MaxFailedAttempts, int.MaxValue);

            // Clamp the shift exponent: C# masks int shift counts to 5 bits, so 1 << 31 is negative
            // and 1 << 32 wraps back to 1, silently shrinking (or negating) the lockout TTL for a
            // sufficiently persistent attacker. 1 << 30 already exceeds any permitted
            // MaxLockoutSeconds (range caps at 3600), so deep excess always lands on the cap.
            var lockoutSeconds = Math.Min(1 << Math.Min(excessAttempts, 30), _settings.MaxLockoutSeconds);
            await cacheService.SetAsync(LockoutKey(email), true, TimeSpan.FromSeconds(lockoutSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ResetFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        await cacheService.RemoveAsync(AttemptsKey(email), cancellationToken).ConfigureAwait(false);
        await cacheService.RemoveAsync(LockoutKey(email), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> CheckRegistrationRateLimitAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return Result.Success();
        }

        var key = RegistrationKey(ipAddress);
        var registrationCount = await cacheService.GetAsync<long?>(key, cancellationToken).ConfigureAwait(false) ?? 0;

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

        // Atomic. On a store with a native counter (Redis) the TTL is also anchored to the first
        // registration in the window; the read-modify-write fallback keeps the previous
        // refresh-on-every-write behavior, which makes the window slide but only ever tightens
        // the limit.
        await cacheService.IncrementAsync(
            RegistrationKey(ipAddress),
            TimeSpan.FromMinutes(_settings.RegistrationRateLimitWindowMinutes),
            cancellationToken).ConfigureAwait(false);
    }

    private static string RegistrationKey(string ipAddress) => $"registration:ip:{ipAddress}";
}
