namespace MMCA.Common.Infrastructure.Caching;

/// <summary>
/// The argument contract of <see cref="MMCA.Common.Application.Interfaces.ICacheService.RemoveByPrefixAsync"/>,
/// enforced identically by every shipped store so invalidation does not behave differently by
/// backend: a prefix is either empty (every entry) or ends at a <c>:</c> boundary.
/// </summary>
internal static class CachePrefixContract
{
    /// <summary>The character every non-empty invalidation prefix must end with.</summary>
    internal const char Boundary = ':';

    /// <summary>Throws unless <paramref name="prefix"/> is empty or ends with <see cref="Boundary"/>.</summary>
    /// <param name="prefix">The prefix a caller asked to invalidate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is non-empty and does not end with <c>:</c>.</exception>
    internal static void EnsureBoundary(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        if (prefix.Length > 0 && prefix[^1] != Boundary)
        {
            throw new ArgumentException(
                $"A cache invalidation prefix must be empty or end at a '{Boundary}' boundary (for example 'MMCA.Sample.Event:'); got '{prefix}'.",
                nameof(prefix));
        }
    }
}
