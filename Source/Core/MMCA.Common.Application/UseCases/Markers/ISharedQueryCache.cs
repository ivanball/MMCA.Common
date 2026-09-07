namespace MMCA.Common.Application.UseCases.Markers;

/// <summary>
/// Opt-out from the caller-scoped cache key: declares that a query addressed at a single user
/// nevertheless produces a result every caller may be served, so its cache entry is shared.
/// </summary>
/// <remarks>
/// <para>
/// SEC-Common-37. By default a query that implements <c>IUserScopedRequest</c> and
/// <see cref="IQueryCacheable"/> has its cache key prefixed with the target user id, because the far
/// commonest reason to address a query at one user is that its result is that user's own data. That
/// is the safe default: a missing prefix serves one caller's personal data to another, a needless
/// prefix only costs an extra cache entry.
/// </para>
/// <para>
/// Add this marker only for a query whose result genuinely does not vary by caller (a public profile
/// card, a published-count badge). It is a deliberate widening of who may read a cached entry, so
/// state the reason on the query type.
/// </para>
/// </remarks>
public interface ISharedQueryCache;
