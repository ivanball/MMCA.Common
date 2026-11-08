using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// The shared-store side of the passkey ceremonies (ADR-139): the single-use challenges, and the
/// per-account lock that serializes passkey deletes, both over the shared cache and the distributed lock
/// <c>AddCaching</c> registers, so they hold across replicas.
/// <para>
/// <b>A challenge is single-use and short-lived.</b> <see cref="IssueAsync"/> stores the issued options
/// under a random flow id for <see cref="Lifetime"/>; <see cref="RedeemAsync"/> reads and burns that
/// entry inside <c>lock:passkeys:challenge:{flowId}</c> (the OAuth exchange-code pattern), so a completion
/// replayed on any replica finds nothing, whatever its signature.
/// </para>
/// </summary>
/// <param name="cache">The shared store the challenges live in.</param>
/// <param name="distributedLock">Makes each redeem, and each account's deletes, one critical section.</param>
public sealed class PasskeyChallenges(ICacheService cache, IDistributedLock distributedLock)
{
    /// <summary>How long an issued challenge stays redeemable.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string KeyPrefix = "passkeys:challenge:";
    private const int FlowIdLength = 32;

    // Each critical section is one cache read plus one remove, or one count plus one delete, so the lease
    // and wait of the OAuth exchange-code lock fit them too.
    private static readonly TimeSpan LockTimeToLive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <summary>Stores a challenge and returns the flow id the completion must name.</summary>
    /// <param name="challenge">The ceremony, the account (for a registration) and the options as issued.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flow id (32 lower-case hex characters).</returns>
    public async Task<string> IssueAsync(PasskeyChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var flowId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(FlowIdLength / 2));
        await cache
            .SetAsync(KeyPrefix + flowId, JsonSerializer.Serialize(challenge), Lifetime, cancellationToken)
            .ConfigureAwait(false);
        return flowId;
    }

    /// <summary>
    /// Reads and burns a challenge in one critical section; <see langword="null"/> when there is none to
    /// redeem (unknown, expired, already used, malformed, or being redeemed by another request right now).
    /// </summary>
    /// <param name="flowId">The flow id the completion names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The challenge, or <see langword="null"/>.</returns>
    public async Task<PasskeyChallenge?> RedeemAsync(string? flowId, CancellationToken cancellationToken = default)
    {
        if (flowId is not { Length: FlowIdLength } || !flowId.All(char.IsAsciiHexDigitLower))
        {
            return null;
        }

        var key = KeyPrefix + flowId;
        var handle = await distributedLock.TryAcquireAsync("lock:" + key, LockTimeToLive, LockWait, cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            // Contended: another request is redeeming this flow right now, so this one is the replay.
            return null;
        }

        await using (handle.ConfigureAwait(false))
        {
            // The shared store, not a local copy: a flow burned on another replica must be a miss here.
            var stored = await cache.GetFromSharedStoreAsync<string>(key, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<PasskeyChallenge>(stored);
        }
    }

    /// <summary>
    /// Takes the account's passkey lock, so two concurrent deletes of its last two passkeys cannot both
    /// see "one more remains"; <see langword="null"/> when another change holds it past the wait.
    /// </summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lock handle to dispose, or <see langword="null"/>.</returns>
    public Task<IAsyncDisposable?> LockAccountAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        distributedLock.TryAcquireAsync(
            string.Create(CultureInfo.InvariantCulture, $"lock:passkeys:user:{userId}"), LockTimeToLive, LockWait, cancellationToken);
}
