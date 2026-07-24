namespace MMCA.Common.Shared.Concurrency;

/// <summary>
/// Serializes work per logical key across a fixed set of <see cref="SemaphoreSlim"/> stripes.
/// A key is mapped to a stripe by its hash, so the table size is bounded by
/// <see cref="Width"/> regardless of how many distinct keys the process sees.
/// <para>
/// This deliberately replaces the "one semaphore per key in a ConcurrentDictionary" shape, which
/// forces a choice between two defects: removing the entry when the last holder releases opens a
/// window where one caller waits on a semaphore that is no longer in the table while a second
/// caller creates a fresh one (both then run concurrently), and never removing it lets a
/// caller-supplied key (an idempotency key, a parameterized cache key) grow the table without
/// bound. Striping has neither problem. The cost is that two unrelated keys can share a stripe and
/// briefly serialize against each other, which is harmless for the double-check-locking callers
/// this exists for: they re-check their own key's state after acquiring.
/// </para>
/// </summary>
/// <remarks>
/// Instances are thread-safe and intended to be held in a static field for the process lifetime.
/// Stripes are never disposed because the instance outlives every caller.
/// </remarks>
public sealed class KeyedSemaphoreStripe
{
    /// <summary>Default number of stripes: ample concurrency without a meaningful memory cost.</summary>
    public const int DefaultWidth = 256;

    private readonly SemaphoreSlim[] _stripes;

    /// <summary>Initializes a stripe set with <see cref="DefaultWidth"/> stripes.</summary>
    public KeyedSemaphoreStripe()
        : this(DefaultWidth)
    {
    }

    /// <summary>Initializes a stripe set with the given width.</summary>
    /// <param name="width">Number of stripes. Must be greater than zero.</param>
    public KeyedSemaphoreStripe(int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);

        Width = width;
        _stripes = new SemaphoreSlim[width];
        for (var i = 0; i < width; i++)
        {
            _stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>Gets the number of stripes.</summary>
    public int Width { get; }

    /// <summary>
    /// Acquires the stripe guarding <paramref name="key"/>, returning a handle that releases it on
    /// disposal. Await the call inside a <see langword="using"/> statement so the release happens
    /// even when the guarded work throws.
    /// </summary>
    /// <param name="key">The logical key to serialize on.</param>
    /// <param name="cancellationToken">Cancels the wait, not the work that follows it.</param>
    /// <returns>A handle whose disposal releases the stripe.</returns>
    public async Task<Releaser> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        var stripe = GetStripe(key);
        await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(stripe);
    }

    private SemaphoreSlim GetStripe(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Ordinal hash, folded to a non-negative index. int.MinValue has no positive counterpart,
        // so mask the sign bit rather than calling Math.Abs.
        var index = (uint)string.GetHashCode(key, StringComparison.Ordinal) % (uint)Width;
        return _stripes[index];
    }

    /// <summary>Releases the acquired stripe when disposed.</summary>
    public readonly record struct Releaser : IDisposable
    {
        private readonly SemaphoreSlim? _stripe;

        internal Releaser(SemaphoreSlim stripe) => _stripe = stripe;

        /// <summary>Releases the stripe. Safe to call on a default-constructed instance.</summary>
        public void Dispose() => _stripe?.Release();
    }
}
