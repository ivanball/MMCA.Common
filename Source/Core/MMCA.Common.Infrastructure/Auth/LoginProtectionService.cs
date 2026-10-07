using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects.Contact;

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
/// <para>
/// <b>Fails open on a cache outage.</b> The counters live in the cache, and the cache is an
/// optimization that never turns its own outage into an error (<c>CacheSettings</c>). When the cache
/// throws, a check answers success and an increment or reset does nothing, each with a warning log,
/// so an unreachable cache suspends the limits instead of failing every sign-in and registration.
/// Cancellation of the caller's own token still propagates.
/// </para>
/// </summary>
public sealed partial class LoginProtectionService(
    ICacheService cacheService,
    IOptions<LoginProtectionSettings> settings,
    ILogger<LoginProtectionService>? logger = null) : ILoginProtectionService
{
    private readonly LoginProtectionSettings _settings = settings.Value;
    private readonly ILogger _logger = logger ?? NullLogger<LoginProtectionService>.Instance;

    /// <summary>
    /// Normalizes the supplied address the same way <see cref="Email"/> does before it is used in a
    /// counter key. Without this the keys are built from raw request input while the user lookup
    /// runs against the normalized value object, so <c>User@x.com</c>, <c>user@x.com</c> and
    /// <c>" user@x.com "</c> resolve to one account but get independent attempt counters and
    /// lockouts: an attacker defeats the ADR-029 backoff just by varying capitalization.
    /// A malformed address (which never matches a user, but still increments a counter) falls back
    /// to the same trim-and-lowercase shape so its attempts collapse onto one key too.
    /// </summary>
    private static string LockoutKey(string email) => $"login:lockout:{EmailIdentity.Normalize(email)}";

    private static string AttemptsKey(string email) => $"login:attempts:{EmailIdentity.Normalize(email)}";

    /// <inheritdoc />
    public async Task<Result> CheckLockoutAsync(string email, CancellationToken cancellationToken = default)
    {
        var lockoutKey = LockoutKey(email);
        bool isLockedOut;
        try
        {
            // Read from the shared store, never from this replica's L1 copy: a reset clears only the
            // resetting replica's L1, so a local read would keep another replica locking the account
            // out for up to the local cache duration after the lockout was lifted.
            isLockedOut = await cacheService.GetFromSharedStoreAsync<bool?>(lockoutKey, cancellationToken).ConfigureAwait(false) ?? false;
        }
        catch (Exception ex) when (IsCacheOutage(ex, cancellationToken))
        {
            LogCacheUnavailable(_logger, nameof(CheckLockoutAsync), ex);
            return Result.Success();
        }

        return isLockedOut
            ? Result.Failure(Error.TooManyRequests(
                "Auth.TooManyAttempts",
                "Too many failed login attempts. Please try again later.",
                nameof(CheckLockoutAsync)))
            : Result.Success();
    }

    /// <inheritdoc />
    public async Task IncrementFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        // NOT atomic on the distributed cache today: DistributedCacheService.IncrementAsync is a
        // read-modify-write, because the Redis INCR it used to issue wrote a plain string key while
        // IDistributedCache reads entries back as hashes, and the mismatch made the counter
        // unreadable (WRONGTYPE). Readability was the right thing to buy first, but the cost is the
        // known one: parallel attempts can overwrite each other's increments, so a burst of
        // genuinely concurrent guesses can stay below MaxFailedAttempts. Sequential guessing, which
        // is what a credential-stuffing run against one account looks like, still trips the lockout.
        // Closing the gap needs the increment made atomic again WITHIN the hash layout (a Lua
        // script) or counters moved off IDistributedCache so both sides speak Redis strings.
        try
        {
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
        catch (Exception ex) when (IsCacheOutage(ex, cancellationToken))
        {
            LogCacheUnavailable(_logger, nameof(IncrementFailedAttemptsAsync), ex);
        }
    }

    /// <inheritdoc />
    public async Task ResetFailedAttemptsAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            await cacheService.RemoveAsync(AttemptsKey(email), cancellationToken).ConfigureAwait(false);
            await cacheService.RemoveAsync(LockoutKey(email), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCacheOutage(ex, cancellationToken))
        {
            LogCacheUnavailable(_logger, nameof(ResetFailedAttemptsAsync), ex);
        }
    }

    /// <inheritdoc />
    public async Task<Result> CheckRegistrationRateLimitAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return Result.Success();
        }

        var key = RegistrationKey(ipAddress);
        long registrationCount;
        try
        {
            // Read the counter from the shared store, never from a process-local copy: IncrementAsync
            // writes the shared store only, so a hybrid cache's in-process entry would pin the first
            // count it saw and the limit would never trip while that copy lived.
            registrationCount = await cacheService.GetFromSharedStoreAsync<long?>(key, cancellationToken).ConfigureAwait(false) ?? 0;
        }
        catch (Exception ex) when (IsCacheOutage(ex, cancellationToken))
        {
            LogCacheUnavailable(_logger, nameof(CheckRegistrationRateLimitAsync), ex);
            return Result.Success();
        }

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

        // Read-modify-write (see IncrementFailedAttemptsAsync for why the native-counter path was
        // removed), so the TTL is refreshed on every write. That makes the window slide rather than
        // stay anchored to the first registration, which only ever tightens the limit.
        try
        {
            await cacheService.IncrementAsync(
                RegistrationKey(ipAddress),
                TimeSpan.FromMinutes(_settings.RegistrationRateLimitWindowMinutes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCacheOutage(ex, cancellationToken))
        {
            LogCacheUnavailable(_logger, nameof(IncrementRegistrationCountAsync), ex);
        }
    }

    private static string RegistrationKey(string ipAddress) => $"registration:ip:{ipAddress}";

    /// <summary>
    /// Every cache fault counts as an outage except the caller's own cancellation, which keeps
    /// propagating. A cancellation the caller did not request (a store-side timeout) is an outage.
    /// </summary>
    private static bool IsCacheOutage(Exception exception, CancellationToken cancellationToken) =>
        exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested;

    // The operation name is logged, never the key: the keys carry the email address or client IP.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Login protection could not reach the cache during {Operation}; failing open, so this call applies no lockout and no registration limit.")]
    private static partial void LogCacheUnavailable(ILogger logger, string operation, Exception exception);
}
