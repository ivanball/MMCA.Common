namespace MMCA.Common.Application.Settings;

/// <summary>
/// The Application layer's view of the <c>Cache</c> configuration section: the one knob the CQRS
/// caching pipeline needs, exposed here because <c>CachingQueryDecorator</c> lives in this layer and
/// this layer cannot reference Infrastructure, where the rest of the section is bound.
/// </summary>
/// <remarks>
/// <para>
/// Binding is Infrastructure's job (<c>AddCaching</c> binds this type alongside its own
/// <c>CacheSettings</c>), so both read the same <c>Cache:PopulateLockTimeout</c> key and cannot
/// drift. A host that never calls <c>AddCaching</c>, and a decorator constructed by hand, both fall
/// back to <see cref="DefaultPopulateLockTimeout"/>.
/// </para>
/// <para>
/// Fail-open, like every other cache knob: the value bounds how long a request waits, never whether
/// it succeeds. A waiter that gives up runs the real handler and answers correctly, just uncached.
/// </para>
/// </remarks>
public sealed class QueryCachePipelineSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "Cache";

    /// <summary>
    /// The wait applied when nothing is configured: unbounded, which is the behavior the per-key
    /// populate lock had before this setting existed.
    /// </summary>
    public static readonly TimeSpan DefaultPopulateLockTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets how long a request that missed the cache waits for the per-key populate lock before
    /// giving up and executing the inner handler uncached.
    /// </summary>
    /// <remarks>
    /// The default waits indefinitely, so exactly one request per key populates the entry and the
    /// rest are served from it (stampede protection). A finite value bounds the wait instead: once
    /// it elapses the waiter proceeds uncached, so a pathologically slow populate cannot hold a
    /// queue of requests behind it. The trade is that several requests may then run the same query
    /// at once. Zero or a negative value means no bound, exactly like the default.
    /// </remarks>
    public TimeSpan PopulateLockTimeout { get; init; } = DefaultPopulateLockTimeout;
}
