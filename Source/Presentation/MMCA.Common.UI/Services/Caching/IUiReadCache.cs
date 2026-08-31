using System.Diagnostics.CodeAnalysis;

namespace MMCA.Common.UI.Services.Caching;

/// <summary>
/// Per-circuit read-through cache over the API client: the client half of the framework's caching
/// policy, so a page that re-reads the same list twice inside a few seconds (a grid re-mounted by
/// navigation, a lookup rendered in two components) does not pay for two round trips.
/// <para>
/// <b>Keys are the relative URL, path plus the FULL query string</b>, which is deliberately the same
/// key shape the server's authenticated output cache uses (ADR-040: its policy sets
/// <c>CacheVaryByRules.QueryKeys = "*"</c>, so every query-string variant is its own entry). Mirroring
/// that shape means the two tiers agree on what "the same read" is: a filter, page or sort change
/// misses on both sides rather than serving a stale answer on one of them.
/// </para>
/// <para>
/// Freshness comes from <see cref="MMCA.Common.UI.Common.Settings.UiReadCacheOptions"/>: a default
/// TTL, plus optional per-route-prefix overrides for reads whose staleness budget genuinely differs
/// (reference data can hold for minutes, a live queue for seconds). Only successful reads are stored:
/// a failure is never cached, so a transient outage cannot pin an error in front of the user.
/// </para>
/// <para>
/// Registered scoped, which is one instance per Blazor Server circuit and one per app lifetime on
/// WebAssembly and MAUI. Because the WASM scope outlives a sign-out, the sign-out path calls
/// <see cref="Clear"/> so one account's reads can never be served to the next.
/// </para>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1054:URI-like parameters should not be strings",
    Justification = "The parameter is a cache KEY that happens to be spelled as a relative URL: it is compared by ordinal prefix and stored verbatim so it matches the server-side output-cache key shape (path + full query, ADR-040). System.Uri would re-encode and re-normalize the string, which is exactly what must not happen to a key, and every call site already holds the same string it puts on the wire.")]
public interface IUiReadCache
{
    /// <summary>
    /// Reads a cached value that is still within its TTL.
    /// </summary>
    /// <typeparam name="T">The cached value's type; a hit stored under a different type reads as a miss.</typeparam>
    /// <param name="url">The relative request URL (path plus the full query string), used verbatim as the key.</param>
    /// <param name="value">The cached value on a hit; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> on a fresh hit, <see langword="false"/> on a miss, an expired
    /// entry, or when caching is disabled.</returns>
    bool TryGetFresh<T>(string url, out T? value);

    /// <summary>
    /// Stores a successfully read value under <paramref name="url"/>, stamped with the current time.
    /// A no-op when caching is disabled.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="url">The relative request URL (path plus the full query string), used verbatim as the key.</param>
    /// <param name="value">The value to cache. Only ever a success value: failures are not stored.</param>
    void Set<T>(string url, T value);

    /// <summary>
    /// Drops every entry whose key starts with <paramref name="routePrefix"/> (ordinal), which is how
    /// a successful write invalidates the reads it just made stale: one endpoint's create, update or
    /// delete clears that endpoint's list, paged, lookup and by-id entries in one call.
    /// </summary>
    /// <param name="routePrefix">The endpoint's relative route prefix, e.g. <c>products</c>.</param>
    void InvalidatePrefix(string routePrefix);

    /// <summary>
    /// Drops every entry. Called on sign-out, where the scope can outlive the session (WebAssembly and
    /// MAUI resolve one scope for the app's lifetime) and a surviving entry would be one account's data
    /// shown to the next.
    /// </summary>
    void Clear();
}
