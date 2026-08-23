using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Implements the forgot-password token lifecycle over <see cref="ICacheService"/>, so the feature
/// needs no schema change and expired tokens are reaped by cache TTL rather than a sweeper.
/// <list type="bullet">
///   <item><b>One active token per email</b>: issuing overwrites the previous record, so an older
///     link stops working the moment a newer one is requested.</item>
///   <item><b>Hashed at rest</b>: only the SHA-256 of the token is stored, so a cache dump does not
///     hand out working reset links.</item>
///   <item><b>Attempt cap</b>: wrong tokens are counted and the record is discarded at
///     <see cref="PasswordResetSettings.MaxValidationAttempts"/>.</item>
///   <item><b>Per-email request throttle</b>: a counter with the request window's TTL caps how often
///     one address can trigger an email.</item>
/// </list>
/// </summary>
public sealed class PasswordResetTokenService(
    ICacheService cacheService,
    IOptions<PasswordResetSettings> settings) : IPasswordResetTokenService
{
    private const int TokenByteLength = 32;

    private readonly PasswordResetSettings _settings = settings.Value;

    /// <summary>
    /// Normalizes the supplied address the same way <see cref="Email"/> does before it is used in a
    /// key, for the reason documented on <see cref="LoginProtectionService"/>: keys built from raw
    /// request input would give <c>User@x.com</c> and <c>user@x.com</c> independent tokens and
    /// independent request counters while resolving to one account.
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

    private static string TokenKey(string email) => $"pwdreset:token:{NormalizeIdentity(email)}";

    private static string RequestKey(string email) => $"pwdreset:req:{NormalizeIdentity(email)}";

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <inheritdoc />
    public async Task<Result<string>> IssueAsync(
        string email,
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        // Read-modify-write on the distributed cache (the LoginProtectionService gap, documented
        // there): concurrent requests can undercount, which loosens the throttle but never tightens it.
        long requests = await cacheService.IncrementAsync(
            RequestKey(email),
            TimeSpan.FromMinutes(_settings.RequestWindowMinutes),
            cancellationToken).ConfigureAwait(false);

        if (requests > _settings.MaxRequestsPerEmail)
        {
            return Result.Failure<string>(Error.Unauthorized(
                "Auth.ResetThrottled",
                "Too many password reset requests. Please try again later.",
                nameof(IssueAsync)));
        }

        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        var lifetime = TimeSpan.FromMinutes(_settings.TokenLifetimeMinutes);

        var entry = new PasswordResetEntry(
            Convert.ToBase64String(HashToken(token)),
            userId,
            FailedAttempts: 0,
            DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds());

        await cacheService.SetAsync(TokenKey(email), entry, lifetime, cancellationToken).ConfigureAwait(false);

        return Result.Success(token);
    }

    /// <inheritdoc />
    public async Task<Result<UserIdentifierType>> ValidateAndConsumeAsync(
        string email,
        string token,
        CancellationToken cancellationToken = default)
    {
        string key = TokenKey(email);
        var entry = await cacheService.GetAsync<PasswordResetEntry>(key, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return InvalidToken();
        }

        byte[] stored;
        try
        {
            stored = Convert.FromBase64String(entry.TokenHashBase64);
        }
        catch (FormatException)
        {
            // An unreadable record can never be redeemed; drop it rather than leaving it to expire.
            await cacheService.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return InvalidToken();
        }

        if (!CryptographicOperations.FixedTimeEquals(HashToken(token ?? string.Empty), stored))
        {
            await RecordFailedAttemptAsync(key, entry, cancellationToken).ConfigureAwait(false);
            return InvalidToken();
        }

        // Consume: the token and the address's request counter both go, so a successful reset does
        // not leave the user throttled out of a later legitimate request.
        await cacheService.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        await cacheService.RemoveAsync(RequestKey(email), cancellationToken).ConfigureAwait(false);

        return Result.Success(entry.UserId);
    }

    private async Task RecordFailedAttemptAsync(
        string key,
        PasswordResetEntry entry,
        CancellationToken cancellationToken)
    {
        int attempts = entry.FailedAttempts + 1;
        long remainingSeconds = entry.ExpiresAtUnixSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (attempts >= _settings.MaxValidationAttempts || remainingSeconds <= 0)
        {
            await cacheService.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Rewritten with the REMAINING lifetime, not a fresh one: a wrong guess must not be able to
        // extend how long the token stays redeemable.
        await cacheService.SetAsync(
            key,
            entry with { FailedAttempts = attempts },
            TimeSpan.FromSeconds(remainingSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    private static Result<UserIdentifierType> InvalidToken() =>
        Result.Failure<UserIdentifierType>(Error.Unauthorized(
            "Auth.InvalidResetToken",
            "The reset link is invalid or has expired. Please request a new one.",
            nameof(ValidateAndConsumeAsync)));
}

/// <summary>
/// The cached reset record. Every member is a JSON primitive: the cache round-trips values through
/// <c>System.Text.Json</c>, so a value object or a <c>byte[]</c> here would not survive a
/// distributed backing store.
/// </summary>
/// <param name="TokenHashBase64">Base64 of the SHA-256 of the issued token (never the token itself).</param>
/// <param name="UserId">The account the token redeems to.</param>
/// <param name="FailedAttempts">Wrong tokens presented against this record so far.</param>
/// <param name="ExpiresAtUnixSeconds">When the record expires, so a rewrite keeps the original lifetime.</param>
internal sealed record PasswordResetEntry(
    string TokenHashBase64,
    UserIdentifierType UserId,
    int FailedAttempts,
    long ExpiresAtUnixSeconds);
