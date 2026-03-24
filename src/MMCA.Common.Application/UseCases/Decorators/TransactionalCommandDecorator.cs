using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Application.UseCases.Decorators;

/// <summary>
/// Decorator that wraps command execution in a database transaction when the command
/// implements the <see cref="ITransactional"/> marker interface. Commands that do not
/// implement <see cref="ITransactional"/> pass through without transactional wrapping.
/// <para>
/// On success the transaction is committed; on any exception it is rolled back before
/// the exception propagates. This ensures atomicity for multi-step mutations (e.g. creating
/// an order with inventory reservations) without requiring explicit transaction management
/// in each handler.
/// </para>
/// </summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type returned by the handler.</typeparam>
public sealed class TransactionalCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IUnitOfWork unitOfWork) : ICommandHandler<TCommand, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        // Only wrap in a transaction if the command opts in via the ITransactional marker
        if (command is not ITransactional)
            return await inner.HandleAsync(command, cancellationToken);

        unitOfWork.BeginTransaction();
        try
        {
            var result = await inner.HandleAsync(command, cancellationToken);
            unitOfWork.CommitTransaction();
            return result;
        }
        catch
        {
            unitOfWork.RollbackTransaction();
            throw;
        }
    }
}
