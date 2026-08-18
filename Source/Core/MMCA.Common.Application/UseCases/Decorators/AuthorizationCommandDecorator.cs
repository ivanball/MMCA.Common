using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that checks the command's required permission before executing the inner handler.
/// Commands that do not implement <see cref="IRequiresPermission"/> pass through unchanged. When
/// none of the caller's roles grants the permission, returns a failure result with
/// <see cref="ErrorType.Forbidden"/> without invoking the handler.
/// <para>
/// Registered directly inside the feature gate and outside logging, so a denied command never
/// starts a transaction, never touches the cache, and never runs validation, while a disabled
/// feature is still rejected first (a feature that is off must not leak the existence of the
/// permission that guards it).
/// </para>
/// <para>
/// This is a defense in depth layer, not a replacement for the endpoint's <c>[Authorize]</c>
/// policy: it moves the capability check next to the use case, so a command reached through a new
/// transport (gRPC, a scheduled job, another module) is checked the same way it is over HTTP.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type (typically <see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed class AuthorizationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICurrentUserService currentUser,
    IPermissionRegistry permissionRegistry) : ICommandHandler<TCommand, TResult>
{
    /// <summary>
    /// Cached delegate that creates a <typeparamref name="TResult"/> failure from a collection of
    /// <see cref="Error"/> instances. Built once per generic type instantiation via reflection
    /// to avoid per-call reflection overhead.
    /// </summary>
    /// <remarks>
    /// Built on the first short-circuit rather than in the static constructor, for the same reason
    /// as <see cref="FeatureGateCommandDecorator{TCommand,TResult}"/>:
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
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        if (command is not IRequiresPermission requiresPermission)
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        if (permissionRegistry.HasPermission(currentUser.Roles, requiresPermission.Permission))
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        var commandName = typeof(TCommand).Name;
        CqrsMetrics.RecordAuthorizationDenied(commandName);

        var createFailure = CreateFailure();
        return createFailure([Error.Forbidden(
            "Authorization.PermissionDenied",
            $"The current user does not hold the '{requiresPermission.Permission}' permission.",
            source: commandName)]);
    }
}
