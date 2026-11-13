using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Evicts output-cache tags after a successful write, declared on the action or the controller instead
/// of a hand-written eviction call before every return.
/// </summary>
/// <remarks>
/// <para>
/// <b>When it evicts.</b> As a result filter it runs after the action filters, so it sees the final
/// outcome: after <see cref="Concurrency.SupportsIfMatchAttribute"/> rewrote a conflict to 412 and after
/// an idempotent replay was produced. A 2xx result (read from the result's own status code, else the
/// response's) evicts every tag BEFORE the response body is written; a non-2xx result, a GET or HEAD
/// request, and an unhandled action exception (which skips result filters) evict nothing.
/// </para>
/// <para>
/// <b>Best effort.</b> Eviction goes through <c>TryEvictTagsAsync</c> under
/// <see cref="CancellationToken.None"/>: the write has committed, so a cache-store fault is logged as a
/// Warning (through <c>ILogger&lt;EvictOutputCacheAttribute&gt;</c>) and never fails the request. A host
/// with no <see cref="IOutputCacheStore"/> registered evicts nothing.
/// </para>
/// <para>
/// <b>Once per request.</b> Evicted tags are recorded in <see cref="HttpContext.Items"/> under
/// <see cref="EvictedTagsItemKey"/>, so a class-level and a method-level instance naming the same tag
/// evict it once. Tags compare ordinally.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class EvictOutputCacheAttribute : Attribute, IAsyncResultFilter
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key of the <see cref="HashSet{T}"/> (ordinal) of tags already
    /// evicted for the current request.
    /// </summary>
    public const string EvictedTagsItemKey = "MMCA.Common.API.Caching.EvictedOutputCacheTags";

    private readonly string[] _tags;

    /// <summary>
    /// Initializes a new instance of the <see cref="EvictOutputCacheAttribute"/> class.
    /// </summary>
    /// <param name="tags">The output-cache tags a successful write evicts, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tags"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tags"/> is empty, or a tag is null, empty or whitespace.</exception>
    public EvictOutputCacheAttribute(params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        if (tags.Length == 0)
            throw new ArgumentException("Name at least one output-cache tag to evict.", nameof(tags));

        if (tags.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("An output-cache tag cannot be null, empty or whitespace.", nameof(tags));

        _tags = [.. tags];
    }

    /// <summary>Gets the output-cache tags a successful write evicts, in order.</summary>
    public IReadOnlyList<string> Tags => _tags;

    /// <summary>
    /// Evicts the tags not yet evicted for this request when the request is a write and the final
    /// status is 2xx, then lets the result execute.
    /// </summary>
    /// <param name="context">The executing result context.</param>
    /// <param name="next">The rest of the result pipeline.</param>
    /// <returns>A task that completes when the result has executed.</returns>
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (ShouldEvict(context))
        {
            await EvictAsync(context.HttpContext).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>Whether the request is a write whose final status is 2xx.</summary>
    private static bool ShouldEvict(ResultExecutingContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            return false;

        var statusCode = (context.Result as IStatusCodeActionResult)?.StatusCode ?? context.HttpContext.Response.StatusCode;
        return statusCode is >= 200 and <= 299;
    }

    /// <summary>Evicts, best effort, every tag this request has not already evicted, and records them.</summary>
    private async Task EvictAsync(HttpContext httpContext)
    {
        var evicted = EvictedTagsOf(httpContext);
        var pending = _tags.Where(evicted.Add).ToArray();
        if (pending.Length == 0)
            return;

        var store = httpContext.RequestServices?.GetService<IOutputCacheStore>();
        if (store is null)
            return;

        var logger = (ILogger?)httpContext.RequestServices?.GetService<ILogger<EvictOutputCacheAttribute>>()
            ?? NullLogger<EvictOutputCacheAttribute>.Instance;

        await store.TryEvictTagsAsync(logger, pending).ConfigureAwait(false);
    }

    /// <summary>The request's set of already-evicted tags, created and stored on first use.</summary>
    private static HashSet<string> EvictedTagsOf(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(EvictedTagsItemKey, out var recorded) && recorded is HashSet<string> existing)
            return existing;

        var created = new HashSet<string>(StringComparer.Ordinal);
        httpContext.Items[EvictedTagsItemKey] = created;
        return created;
    }
}
