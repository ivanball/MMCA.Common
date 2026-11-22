using System.Runtime.CompilerServices;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Auth.TwoFactor;

/// <summary>
/// Carries "the API refused this session for want of a second factor" from the API client's handler
/// to the page that should react to it. The handler runs in the <c>IHttpClientFactory</c> handler
/// scope, not the component's, so the signal is keyed by the token storage that attached the bearer
/// (the same owner <see cref="AuthDelegatingHandler"/> reports a 401 to), and the subscriber is the
/// listener rendered in that storage's scope.
/// </summary>
/// <remarks>
/// Backed by a <see cref="ConditionalWeakTable{TKey, TValue}"/>: the scope's storage instance is the key,
/// so an ended circuit or scope is never kept alive by a subscription. One subscriber per storage; a
/// later subscription replaces an earlier one, and disposing a replaced subscription leaves its
/// successor in place.
/// </remarks>
internal static class MultiFactorRequiredSignal
{
    private static readonly ConditionalWeakTable<ITokenStorageService, Subscription> Subscriptions = [];

    /// <summary>Subscribes <paramref name="onRequired"/> to raises for <paramref name="scopeStorage"/>.</summary>
    /// <param name="scopeStorage">The token storage of the subscriber's scope.</param>
    /// <param name="onRequired">Invoked once per raise.</param>
    /// <returns>A handle that ends the subscription when disposed.</returns>
    internal static IDisposable Subscribe(ITokenStorageService scopeStorage, Action onRequired)
    {
        ArgumentNullException.ThrowIfNull(scopeStorage);
        ArgumentNullException.ThrowIfNull(onRequired);

        var subscription = new Subscription(scopeStorage, onRequired);
        Subscriptions.AddOrUpdate(scopeStorage, subscription);
        return subscription;
    }

    /// <summary>Whether anything listens for raises on <paramref name="owner"/>.</summary>
    /// <param name="owner">The storage that attached the refused bearer.</param>
    /// <returns><see langword="true"/> when a live subscription exists.</returns>
    internal static bool HasSubscriber(ITokenStorageService owner) =>
        Subscriptions.TryGetValue(owner, out _);

    /// <summary>Invokes the subscriber of <paramref name="owner"/>, when there is one.</summary>
    /// <param name="owner">The storage that attached the refused bearer.</param>
    internal static void Raise(ITokenStorageService owner)
    {
        if (Subscriptions.TryGetValue(owner, out var subscription))
        {
            subscription.Invoke();
        }
    }

    /// <summary>One subscriber; disposing it removes it only while it is still the current one.</summary>
    private sealed class Subscription(ITokenStorageService storage, Action onRequired) : IDisposable
    {
        private readonly WeakReference<ITokenStorageService> _storage = new(storage);
        private Action? _onRequired = onRequired;

        public void Invoke() => _onRequired?.Invoke();

        public void Dispose()
        {
            _onRequired = null;
            if (_storage.TryGetTarget(out var target)
                && Subscriptions.TryGetValue(target, out var current)
                && ReferenceEquals(current, this))
            {
                Subscriptions.Remove(target);
            }
        }
    }
}
