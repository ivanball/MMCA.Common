using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Services.Query;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// Composes the EF query tag every repository-built statement carries, so a statement captured in a
/// query store, a slow-query log or a profiler names the code path that issued it instead of being
/// one more anonymous <c>SELECT</c>.
/// <para>
/// Two facts can be known, and either may be absent. The use case comes from the ambient
/// <see cref="QueryTagScope"/> the logging decorator opens; the detail comes from the query builder
/// itself (a specification's type name, or the keyset paging path). A query that knows both is
/// tagged <c>handler:X spec:Y</c>, one that knows one is tagged with that one, and a query that
/// knows neither is returned untouched, without so much as a string allocation.
/// </para>
/// </summary>
internal static class QueryTags
{
    /// <summary>Prefix of the ambient use-case half of a tag.</summary>
    internal const string HandlerPrefix = "handler:";

    /// <summary>Prefix of the specification half of a tag.</summary>
    internal const string SpecificationPrefix = "spec:";

    /// <summary>Prefix marking a keyset ("seek") page.</summary>
    internal const string KeysetPrefix = "keyset:";

    /// <summary>
    /// Tags a query with the ambient use case and the supplied detail.
    /// </summary>
    /// <typeparam name="TEntity">The queried entity type.</typeparam>
    /// <param name="query">The query to tag.</param>
    /// <param name="detail">
    /// The builder's own half of the tag, already prefixed (for example <c>spec:ActiveSpeakersSpec</c>),
    /// or <see langword="null"/> when the builder has nothing to add.
    /// </param>
    /// <returns>The tagged query, or the same instance when there is nothing to say.</returns>
    internal static IQueryable<TEntity> Tag<TEntity>(IQueryable<TEntity> query, string? detail)
        where TEntity : class
    {
        var tag = Compose(QueryTagScope.Current, detail);

        // EF throws on an empty tag, and an untagged query is the correct outcome when neither half
        // is known, so the null check is the behavior rather than a guard against it.
        return tag is null ? query : query.TagWith(tag);
    }

    /// <summary>
    /// Builds the tag text from the two halves.
    /// </summary>
    /// <param name="handlerName">The ambient use-case name, or <see langword="null"/>.</param>
    /// <param name="detail">The already-prefixed builder detail, or <see langword="null"/>.</param>
    /// <returns>The tag, or <see langword="null"/> when neither half is known.</returns>
    internal static string? Compose(string? handlerName, string? detail)
    {
        var hasHandler = !string.IsNullOrWhiteSpace(handlerName);
        var hasDetail = !string.IsNullOrWhiteSpace(detail);

        return (hasHandler, hasDetail) switch
        {
            (true, true) => $"{HandlerPrefix}{handlerName} {detail}",
            (true, false) => HandlerPrefix + handlerName,
            (false, true) => detail,
            _ => null,
        };
    }
}
