using System.Security.Cryptography;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;

namespace MMCA.Common.UI.Services.Auth.OAuth;

/// <summary>
/// Binds an OAuth completion to the flow this client started.
/// <para>
/// SECURITY: the completion code the provider round trip hands back is a bearer value; whoever
/// redeems it first is signed in as the account it was minted for. Without a binding, an attacker
/// who completes the provider flow with their OWN account and then sends the victim the completion
/// link signs the victim's app in as the attacker, and everything the victim types next lands in
/// the attacker's account. This store writes a random per-attempt value before the challenge and
/// requires it back at the completion, so a code that arrives without a matching local attempt is
/// dropped instead of exchanged.
/// </para>
/// </summary>
/// <param name="store">Device-local storage; the value must survive a full redirect, so it is not in memory.</param>
/// <param name="timeProvider">Clock used to expire an abandoned attempt; defaults to the system clock.</param>
public sealed class OAuthFlowStateStore(ILocalCacheStore store, TimeProvider? timeProvider = null)
{
    /// <summary>The key the pending attempt is stored under.</summary>
    public const string StorageKey = "auth.oauth-flow";

    /// <summary>
    /// How long a started attempt stays redeemable. Comfortably longer than a provider round trip
    /// and far shorter than a session, so an abandoned attempt cannot be revived days later.
    /// </summary>
    private static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Whether the binding can be enforced on this host. False only where no device-local storage
    /// exists at all (a host that registered neither the browser nor the native capability set);
    /// there the completion keeps its previous behavior because nothing durable can be written
    /// across the redirect.
    /// </summary>
    public bool IsEnforced => store.IsAvailable;

    /// <summary>
    /// Starts an attempt: mints a random value, persists it, and returns it for the caller to send
    /// on the challenge URL. Returns <see langword="null"/> when the host has no durable storage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string?> BeginAsync(CancellationToken cancellationToken = default)
    {
        if (!store.IsAvailable)
        {
            return null;
        }

        var state = RandomNumberGenerator.GetHexString(32, lowercase: true);
        await store
            .SetAsync(StorageKey, new PendingAttempt(state, _timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);

        return state;
    }

    /// <summary>
    /// Consumes the pending attempt and reports whether <paramref name="returnedState"/> may be
    /// exchanged. The attempt is removed either way, so a value is good for exactly one completion.
    /// </summary>
    /// <param name="returnedState">The state the completion redirect carried, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the completion belongs to an attempt this client started.</returns>
    public async Task<bool> TryCompleteAsync(string? returnedState, CancellationToken cancellationToken = default)
    {
        if (!store.IsAvailable)
        {
            return true;
        }

        var pending = await store.GetAsync<PendingAttempt>(StorageKey, cancellationToken).ConfigureAwait(false);
        await store.RemoveAsync(StorageKey, cancellationToken).ConfigureAwait(false);

        if (pending is null || _timeProvider.GetUtcNow() - pending.StartedAt > AttemptLifetime)
        {
            return false;
        }

        // An empty returned state means the redirect could not carry one back; the attempt marker
        // is then the whole binding. A non-empty one must match exactly.
        return string.IsNullOrEmpty(returnedState)
            || string.Equals(pending.State, returnedState, StringComparison.Ordinal);
    }

    private sealed record PendingAttempt(string State, DateTimeOffset StartedAt);
}
