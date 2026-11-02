using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.SignInLinks;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Auth;

/// <summary>
/// Implements the sign-in-link token lifecycle over <see cref="ICacheService"/>, on the design
/// <see cref="EmailConfirmationTokenService"/> uses, so the feature needs no schema change and expired
/// tokens are reaped by cache TTL rather than a sweeper.
/// <list type="bullet">
///   <item><b>One active token per address and purpose</b>: issuing overwrites the previous record of
///     that purpose, so an older link stops working the moment a newer one is requested, while a
///     sign-in request leaves an outstanding invitation alone.</item>
///   <item><b>Hashed at rest</b>: only the SHA-256 of the token is stored.</item>
///   <item><b>Attempt cap</b>: a wrong token counts against every outstanding record of the address,
///     and a record is discarded at <see cref="SignInLinkSettings.MaxValidationAttempts"/>.</item>
///   <item><b>Per-address request throttle</b>: a counter with the request window's TTL caps how
///     often one address can trigger an email.</item>
/// </list>
/// </summary>
/// <remarks>
/// The cache keys carry their own <c>signinlink:</c> prefix, for the reason the confirmation service
/// keeps its own: a shared key would let one workflow silently invalidate another's outstanding link.
/// <para>
/// Redemption takes no <see cref="IDistributedLock"/>. The read, compare and remove can race, and two
/// concurrent redemptions of one token would then both open a session for the same account the link
/// already proved. That is the outcome a double tap on the link asks for, so the lock would buy
/// nothing a second session does not already give the same owner.
/// </para>
/// </remarks>
/// <param name="cacheService">The cache the token records live in.</param>
/// <param name="settings">The bound sign-in-link settings.</param>
/// <param name="timeProvider">Clock for the record expiry stamp and the remaining-lifetime check.</param>
public sealed class SignInLinkTokenService(
    ICacheService cacheService,
    IOptions<SignInLinkSettings> settings,
    TimeProvider timeProvider) : ISignInLinkTokenService
{
    private const int TokenByteLength = 32;

    private static readonly SignInLinkPurpose[] Purposes = [SignInLinkPurpose.SignIn, SignInLinkPurpose.Invite];

    private readonly SignInLinkSettings _settings = settings.Value;

    /// <inheritdoc />
    public async Task<Result<string>> IssueAsync(
        string email,
        SignInLinkPurpose purpose,
        SignInLinkProfile? profile = null,
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
                "Authentication.SignInLinkThrottled",
                "Too many sign-in link requests. Please try again later.",
                nameof(IssueAsync)));
        }

        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        var lifetime = _settings.LifetimeFor(purpose);

        var entry = new SignInLinkEntry(
            Convert.ToBase64String(HashToken(token)),
            purpose,
            profile?.FirstName,
            profile?.LastName,
            FailedAttempts: 0,
            timeProvider.GetUtcNow().Add(lifetime).ToUnixTimeSeconds());

        await cacheService.SetAsync(TokenKey(email, purpose), entry, lifetime, cancellationToken).ConfigureAwait(false);

        return Result.Success(token);
    }

    /// <inheritdoc />
    public async Task<Result<SignInLinkGrant>> ValidateAndConsumeAsync(
        string email,
        string token,
        CancellationToken cancellationToken = default)
    {
        byte[] presented = HashToken(token ?? string.Empty);
        var outstanding = new List<(string Key, SignInLinkEntry Entry)>(Purposes.Length);

        foreach (var purpose in Purposes)
        {
            string key = TokenKey(email, purpose);

            // Shared-store read: a token consumed on another replica must be a miss here, never a
            // stale local copy that would let it be redeemed twice.
            var entry = await cacheService.GetFromSharedStoreAsync<SignInLinkEntry>(key, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            if (!TryReadHash(entry, out var stored))
            {
                // An unreadable record can never be redeemed; drop it rather than leaving it to expire.
                await cacheService.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presented, stored))
            {
                // Consume: the matched token and the address's request counter both go, so a signed-in
                // owner is not left throttled out of the next legitimate request.
                await cacheService.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                await cacheService.RemoveAsync(RequestKey(email), cancellationToken).ConfigureAwait(false);

                return Result.Success(new SignInLinkGrant(entry.Purpose, ProfileOf(entry)));
            }

            outstanding.Add((key, entry));
        }

        foreach (var (key, entry) in outstanding)
        {
            await RecordFailedAttemptAsync(key, entry, cancellationToken).ConfigureAwait(false);
        }

        return Result.Failure<SignInLinkGrant>(SignInLinkErrors.InvalidLink(nameof(ValidateAndConsumeAsync)));
    }

    /// <summary>
    /// Normalizes the supplied address the same way the other token services do before it is used in
    /// a key, so <c>User@x.com</c> and <c>user@x.com</c> share one token and one request counter.
    /// </summary>
    private static string TokenKey(string email, SignInLinkPurpose purpose) =>
        $"signinlink:token:{((int)purpose).ToString(CultureInfo.InvariantCulture)}:{EmailIdentity.KeySegment(email)}";

    private static string RequestKey(string email) => $"signinlink:req:{EmailIdentity.KeySegment(email)}";

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static bool TryReadHash(SignInLinkEntry entry, out byte[] hash)
    {
        try
        {
            hash = Convert.FromBase64String(entry.TokenHashBase64);
            return true;
        }
        catch (FormatException)
        {
            hash = [];
            return false;
        }
    }

    private static SignInLinkProfile? ProfileOf(SignInLinkEntry entry) =>
        string.IsNullOrWhiteSpace(entry.FirstName) && string.IsNullOrWhiteSpace(entry.LastName)
            ? null
            : new SignInLinkProfile(entry.FirstName ?? string.Empty, entry.LastName ?? string.Empty);

    private async Task RecordFailedAttemptAsync(
        string key,
        SignInLinkEntry entry,
        CancellationToken cancellationToken)
    {
        int attempts = entry.FailedAttempts + 1;
        long remainingSeconds = entry.ExpiresAtUnixSeconds - timeProvider.GetUtcNow().ToUnixTimeSeconds();

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
}

/// <summary>
/// The cached sign-in-link record. Every member is a JSON primitive (or an enum), because the cache
/// round-trips values through <c>System.Text.Json</c>.
/// </summary>
/// <param name="TokenHashBase64">Base64 of the SHA-256 of the issued token (never the token itself).</param>
/// <param name="Purpose">Why the link was sent.</param>
/// <param name="FirstName">The provisioning first name an invitation carries, or null.</param>
/// <param name="LastName">The provisioning last name an invitation carries, or null.</param>
/// <param name="FailedAttempts">Wrong tokens presented against this record so far.</param>
/// <param name="ExpiresAtUnixSeconds">When the record expires, so a rewrite keeps the original lifetime.</param>
internal sealed record SignInLinkEntry(
    string TokenHashBase64,
    SignInLinkPurpose Purpose,
    string? FirstName,
    string? LastName,
    int FailedAttempts,
    long ExpiresAtUnixSeconds);
