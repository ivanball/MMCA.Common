using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.ProcessManagers;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// Runs one process step per domain event: opens ONE scope, runs ONE transaction on its unit of work,
/// loads the correlated process tracked inside it, and hands it to <see cref="HandleStepAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The step owns its saves.</b> The base never calls <c>SaveChangesAsync</c>; the transaction
/// refuses to commit unsaved changes, so a step that forgets to save fails loudly.
/// </para>
/// <para>
/// A step that returns a failed <see cref="Result"/> rolls the unit back, after which the base calls
/// <see cref="OnStepFailed"/>; its default throws <see cref="ProcessStepFailedException"/> so the outbox
/// redelivers the event. An override may return normally to keep the event handled. An exception
/// thrown by the step propagates unchanged, and cancellation is never caught.
/// </para>
/// </remarks>
/// <typeparam name="TDomainEvent">The domain event that drives the step.</typeparam>
/// <typeparam name="TProcess">The process aggregate.</typeparam>
/// <typeparam name="TProcessId">The process aggregate's identifier type.</typeparam>
/// <param name="serviceScopeFactory">Opens the event's scope.</param>
public abstract class ProcessDomainEventHandlerBase<TDomainEvent, TProcess, TProcessId>(IServiceScopeFactory serviceScopeFactory)
    : IDomainEventHandler<TDomainEvent>
    where TDomainEvent : IDomainEvent
    where TProcess : ProcessManagerAggregateRoot<TProcessId>
    where TProcessId : notnull
{
    /// <summary>Gets the navigation paths loaded with the process. Defaults to none.</summary>
    protected virtual IEnumerable<string> Includes => [];

    /// <inheritdoc />
    public async Task HandleAsync(TDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        OnStepStarting(domainEvent);

        var scope = serviceScopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var result = await unitOfWork.ExecuteInTransactionAsync(
                async token =>
                {
                    var process = await unitOfWork.GetRepository<TProcess, TProcessId>()
                        .GetByIdAsync(CorrelateProcessId(domainEvent), Includes, asTracking: true, token)
                        .ConfigureAwait(false);

                    if (process is null)
                    {
                        OnProcessMissing(domainEvent);
                        return Result.Success();
                    }

                    return await HandleStepAsync(
                        process,
                        domainEvent,
                        new ProcessStepContext(scope.ServiceProvider, unitOfWork),
                        token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                OnStepFailed(domainEvent, result.Errors);
            }
        }
    }

    /// <summary>Maps the event to the id of the process it drives.</summary>
    /// <param name="domainEvent">The event.</param>
    /// <returns>The process id.</returns>
    protected abstract TProcessId CorrelateProcessId(TDomainEvent domainEvent);

    /// <summary>
    /// Runs the step on the tracked process inside the open transaction. Save through
    /// <see cref="ProcessStepContext.UnitOfWork"/> before returning.
    /// </summary>
    /// <param name="process">The tracked process.</param>
    /// <param name="domainEvent">The event.</param>
    /// <param name="context">The scope, its unit of work and the timeout scheduler.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success to commit; a failure to roll back and call <see cref="OnStepFailed"/>.</returns>
    protected abstract Task<Result> HandleStepAsync(
        TProcess process,
        TDomainEvent domainEvent,
        ProcessStepContext context,
        CancellationToken cancellationToken);

    /// <summary>Called first, before the scope opens. Does nothing by default.</summary>
    /// <param name="domainEvent">The event.</param>
    protected virtual void OnStepStarting(TDomainEvent domainEvent)
    {
    }

    /// <summary>Called when no process carries the correlated id; the event is then handled. Does nothing by default.</summary>
    /// <param name="domainEvent">The event.</param>
    protected virtual void OnProcessMissing(TDomainEvent domainEvent)
    {
    }

    /// <summary>
    /// Called after a failed step rolled the unit back. The default throws
    /// <see cref="ProcessStepFailedException"/> so the outbox redelivers the event; an override that
    /// returns normally keeps the event handled.
    /// </summary>
    /// <param name="domainEvent">The event.</param>
    /// <param name="errors">The step's errors.</param>
    /// <exception cref="ProcessStepFailedException">Always, in the default implementation.</exception>
    protected virtual void OnStepFailed(TDomainEvent domainEvent, IReadOnlyList<Error> errors) =>
        throw new ProcessStepFailedException(
            typeof(TProcess).Name,
            Convert.ToString(CorrelateProcessId(domainEvent), CultureInfo.InvariantCulture) ?? string.Empty,
            errors);
}
