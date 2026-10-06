namespace MMCA.Common.UI.Common;

/// <summary>
/// The component-lifetime token read. A component owns a <see cref="CancellationTokenSource"/> that
/// its <c>Dispose</c> cancels and disposes, and every awaited call takes its token. Reading
/// <see cref="CancellationTokenSource.Token"/> off a DISPOSED source throws
/// <see cref="ObjectDisposedException"/>, so a load or handler that resumed after the user navigated
/// away crashed the circuit instead of stopping. <see cref="LifetimeToken"/> returns an
/// already-cancelled token instead, and the work stops through its normal
/// <see cref="OperationCanceledException"/> path.
/// </summary>
/// <remarks>
/// Read the token as <c>_cts.LifetimeToken()</c>, never through the source's raw <c>Token</c> property;
/// <c>LifetimeTokenConventionTestsBase</c> in MMCA.Common.Testing.Architecture pins that in each repo.
/// A component that prefers a property may still declare
/// <c>private CancellationToken LifetimeToken =&gt; _cts.LifetimeToken();</c>.
/// </remarks>
public static class ComponentLifetimeExtensions
{
    /// <summary>
    /// Returns the source's token while the source is live, or an already-cancelled token once it has
    /// been cancelled, disposed, or was never created.
    /// </summary>
    /// <param name="source">The component's lifetime source; <see langword="null"/> is treated as ended.</param>
    /// <returns>A token that is safe to read at any point of the component's lifetime.</returns>
    public static CancellationToken LifetimeToken(this CancellationTokenSource? source)
    {
        if (source is null || source.IsCancellationRequested)
        {
            return new CancellationToken(canceled: true);
        }

        try
        {
            return source.Token;
        }
        catch (ObjectDisposedException)
        {
            // Disposed without being cancelled first: the component is gone all the same.
            return new CancellationToken(canceled: true);
        }
    }
}
