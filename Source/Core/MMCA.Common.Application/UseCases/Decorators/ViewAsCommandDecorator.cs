using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Refuses every command while the caller holds a view-as token (ADR-137), so a staff user viewing
/// the app as another user can read but never write in that user's name. The HTTP layer already
/// rejects non-safe methods for such a caller; this is the second layer, and the one that also covers
/// a command sent from a GET handler or a hub method.
/// <para>
/// Registered as the outermost command decorator, so a refused command does no feature-flag lookup,
/// permission check, logging, validation or transaction work. For an ordinary caller it costs one
/// claim lookup and passes straight through.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type (<see cref="Result"/> or <see cref="Result{T}"/>).</typeparam>
public sealed class ViewAsCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ICurrentUserService currentUser) : ICommandHandler<TCommand, TResult>
{
    /// <summary>
    /// Built on the first refusal rather than in a static initializer, for the reason
    /// <see cref="FeatureGateCommandDecorator{TCommand, TResult}"/> documents: an eager build turns an
    /// unsupported <typeparamref name="TResult"/> into a resolve-time failure for every handler.
    /// </summary>
    private static Func<IEnumerable<Error>, TResult>? _createFailure;

    private static Func<IEnumerable<Error>, TResult> CreateFailure()
        => _createFailure ??= ResultFailureFactory.Build<TResult>();

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        if (!currentUser.User.IsViewAs())
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        var createFailure = CreateFailure();
        return createFailure([Error.Forbidden(
            "Auth.ViewAsReadOnly",
            "Viewing as another user is read-only.")]);
    }
}
