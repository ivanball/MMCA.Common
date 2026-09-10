using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Implements the email-confirmation token lifecycle over <see cref="ICacheService"/>, on exactly the
/// design <see cref="PasswordResetTokenService"/> uses, so the feature needs no schema change and
/// expired tokens are reaped by cache TTL rather than a sweeper.
/// <list type="bullet">
///   <item><b>One active token per address</b>: issuing overwrites the previous record, so an older
///     link stops working the moment a newer one is requested.</item>
///   <item><b>Hashed at rest</b>: only the SHA-256 of the token is stored, so a cache dump does not
///     hand out working confirmation links.</item>
///   <item><b>Attempt cap</b>: wrong tokens are counted and the record is discarded at
///     <see cref="EmailConfirmationSettings.MaxValidationAttempts"/>.</item>
///   <item><b>Per-address request throttle</b>: a counter with the request window's TTL caps how
///     often one address can trigger an email.</item>
/// </list>
/// </summary>
/// <remarks>
/// The cache keys carry their own <c>emailconfirm:</c> prefix rather than sharing the reset service's.
/// That separation is load-bearing: a shared key would make issuing a confirmation link silently
/// invalidate an outstanding password-reset link for the same address, and the two have very
/// different lifetimes.
/// </remarks>
/// <param name="cacheService">The cache the token records live in.</param>
/// <param name="settings">The bound email-confirmation settings.</param>
public sealed class EmailConfirmationTokenService(
    ICacheService cacheService,
    IOptions<EmailConfirmationSettings> settings) : IEmailConfirmationTokenService
{
    private const int TokenByteLength = 32;

    private readonly EmailConfirmationSettings _settings = settings.Value;

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
                "Authentication.ConfirmationThrottled",
                "Too many confirmation requests. Please try again later.",
                nameof(IssueAsync)));
        }

        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        var lifetime = TimeSpan.FromMinutes(_settings.TokenLifetimeMinutes);

        var entry = new EmailConfirmationEntry(
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
        var entry = await cacheService.GetAsync<EmailConfirmationEntry>(key, cancellationToken).ConfigureAwait(false);
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

        // Consume: the token and the address's request counter both go, so a confirmed address is not
        // left throttled out of a later legitimate request (a change of address, say).
        await cacheService.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        await cacheService.RemoveAsync(RequestKey(email), cancellationToken).ConfigureAwait(false);

        return Result.Success(entry.UserId);
    }

    /// <summary>
    /// Normalizes the supplied address the same way <see cref="Email"/> does before it is used in a
    /// key, for the reason documented on <see cref="PasswordResetTokenService"/>: keys built from raw
    /// request input would give <c>User@x.com</c> and <c>user@x.com</c> independent tokens and
    /// independent request counters while resolving to one account.
    /// </summary>
    /// <param name="email">The raw address from the request.</param>
    /// <returns>The normalized address.</returns>
    private static string NormalizeIdentity(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        var result = Email.Create(email);
#pragma warning disable CA1308 // Matches Email's own RFC 5321 lowercase normalization.
        return result.IsSuccess ? result.Value!.Value : email.Trim().ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static string TokenKey(string email) => $"emailconfirm:token:{NormalizeIdentity(email)}";

    private static string RequestKey(string email) => $"emailconfirm:req:{NormalizeIdentity(email)}";

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private async Task RecordFailedAttemptAsync(
        string key,
        EmailConfirmationEntry entry,
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
        Result.Failure<UserIdentifierType>(
            EmailConfirmationErrors.InvalidToken(nameof(ValidateAndConsumeAsync)));
}

/// <summary>
/// The cached confirmation record. Every member is a JSON primitive: the cache round-trips values
/// through <c>System.Text.Json</c>, so a value object or a <c>byte[]</c> here would not survive a
/// distributed backing store.
/// </summary>
/// <param name="TokenHashBase64">Base64 of the SHA-256 of the issued token (never the token itself).</param>
/// <param name="UserId">The account the token redeems to.</param>
/// <param name="FailedAttempts">Wrong tokens presented against this record so far.</param>
/// <param name="ExpiresAtUnixSeconds">When the record expires, so a rewrite keeps the original lifetime.</param>
internal sealed record EmailConfirmationEntry(
    string TokenHashBase64,
    UserIdentifierType UserId,
    int FailedAttempts,
    long ExpiresAtUnixSeconds);
