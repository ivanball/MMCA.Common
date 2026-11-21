namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// The reserved key segment for a cache entry that every service sharing the cache must address
/// identically: a record one service writes and another reads, such as the soft-deleted-user marker
/// Identity writes and every service's middleware reads.
/// </summary>
/// <remarks>
/// <para>
/// A shared store qualifies every key with the host's own namespace (<c>Cache:KeyPrefix</c>, or the
/// per-service <c>Application:Namespace</c>), so two services of one deployment never read each
/// other's entries. That is right for nearly every key and wrong for a cross-service record: the
/// writer and the reader would each address their own copy, and the reader would never see the write.
/// A key that starts with <see cref="Prefix"/> is qualified with the cross-service namespace instead,
/// which every service of one deployment shares (<c>Cache:CrossServiceKeyPrefix</c>, or the token
/// audience <c>Jwt:Audience</c>, since the services that accept one audience's tokens are exactly the
/// services that must honor a revocation of them). Every other key keeps the host's own namespace.
/// </para>
/// <para>
/// Such a key is not subject to prefix invalidation: it carries no generation token, so a prefix wipe in
/// one service cannot hide the record from the others. Remove it by its exact key.
/// </para>
/// </remarks>
public static class CrossServiceCacheKeys
{
    /// <summary>The segment a cross-service key starts with.</summary>
    public const string Prefix = "xsvc:";

    /// <summary>Whether <paramref name="key"/> addresses a cross-service record.</summary>
    /// <param name="key">The caller-supplied cache key.</param>
    /// <returns><see langword="true"/> when the key starts with <see cref="Prefix"/>.</returns>
    public static bool IsCrossService(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
