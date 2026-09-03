using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that checks the query's required permission before executing the inner handler.
/// Queries that do not implement <see cref="IRequiresPermission"/> pass through unchanged. When
/// none of the caller's roles grants the permission, returns a failure result with
/// <see cref="ErrorType.Forbidden"/> without invoking the handler.
/// <para>
/// Registered directly inside the feature gate and outside logging and caching, so a denied query
/// neither reads nor populates the cache. That ordering is load-bearing: a cache lookup placed
/// before the permission check would serve another caller's cached rows to a principal who is not
/// allowed to run the query at all.
/// </para>
/// </summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed class AuthorizationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ICurrentUserService currentUser,
    IPermissionRegistry permissionRegistry) : IQueryHandler<TQuery, TResult>
{
    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    /// <remarks>
    /// Built on the first short-circuit rather than in the static constructor, for the same reason
    /// as <see cref="FeatureGateQueryDecorator{TQuery,TResult}"/>:
    /// <see cref="ResultFailureFactory"/> supports only <see cref="Result"/> and
    /// <see cref="Result{T}"/>, and an eager static initializer would turn an unsupported
    /// <typeparamref name="TResult"/> into a <see cref="TypeInitializationException"/> at RESOLVE
    /// time (Scrutor's TryDecorate is unconditional) for a handler that never short-circuits. One
    /// assignment per closed generic type; a benign duplicate build under a race produces an
    /// equivalent delegate. The happy path never touches it.
    /// </remarks>
    private static Func<IEnumerable<Error>, TResult>? _createFailure;

    /// <summary>
    /// Returns the failure factory, building it on first use. Kept static so the lazy assignment is
    /// never a write to a static field from an instance member.
    /// </summary>
    private static Func<IEnumerable<Error>, TResult> CreateFailure()
        => _createFailure ??= ResultFailureFactory.Build<TResult>();

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default)
    {
        if (query is not IRequiresPermission requiresPermission)
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        if (permissionRegistry.HasPermission(currentUser.Roles, requiresPermission.Permission))
            return await inner.HandleAsync(query, cancellationToken).ConfigureAwait(false);

        var queryName = typeof(TQuery).Name;
        CqrsMetrics.RecordAuthorizationDenied(queryName);

        var createFailure = CreateFailure();
        return createFailure([Error.Forbidden(
            "Authorization.PermissionDenied",
            $"The current user does not hold the '{requiresPermission.Permission}' permission.",
            source: queryName)]);
    }
}
