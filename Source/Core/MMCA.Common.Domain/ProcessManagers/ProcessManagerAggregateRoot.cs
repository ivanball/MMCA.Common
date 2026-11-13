using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.ProcessManagers;

/// <summary>
/// Base class for an aggregate that IS the state of a long-running process (a process manager, or
/// saga): its own status columns decide every step, and named timeouts are applied by re-deriving the
/// due instant from what is stored rather than from a separate state row.
/// <para>
/// <b>Behaviour only.</b> The base declares no property and no field, so deriving from it leaves the
/// EF model and every migration snapshot unchanged. Timeouts arrive as consumer-owned internal
/// commands handled by <c>ProcessTimeoutHandlerBase</c>, and each step a domain event drives runs
/// through <c>ProcessDomainEventHandlerBase</c>.
/// </para>
/// </summary>
/// <typeparam name="TIdentifierType">The aggregate's identifier type.</typeparam>
public abstract class ProcessManagerAggregateRoot<TIdentifierType> : AuditableAggregateRootEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>
    /// Gets the UTC instant of the process's last persisted transition: <c>LastModifiedOn</c> when the
    /// aggregate has been modified, <c>CreatedOn</c> otherwise. Timeout windows are measured from it.
    /// </summary>
    /// <returns>The instant of the last transition.</returns>
    /// <remarks>
    /// A method rather than a property on purpose: a property declared here would map into the EF
    /// model of every adopter.
    /// </remarks>
    public DateTime GetLastTransitionOn() => LastTransition(CreatedOn, LastModifiedOn);

    private static DateTime LastTransition(DateTime createdOn, DateTime? lastModifiedOn) => lastModifiedOn ?? createdOn;

    /// <summary>
    /// Decides what a fired <paramref name="timeout"/> does to this process at <paramref name="utcNow"/>.
    /// An override checks the timeout's name and the stored state, returns
    /// <see cref="ProcessTimeoutDecision.NotYetDue"/> when <c>timeout.DueAfter(GetLastTransitionOn())</c>
    /// lies after <paramref name="utcNow"/>, and otherwise applies the transition. Because the due instant
    /// is re-derived from stored state, a stale or re-armed timeout is a no-op by construction.
    /// </summary>
    /// <param name="timeout">The timeout that fired.</param>
    /// <param name="utcNow">The current UTC instant.</param>
    /// <returns>The decision; the default is <see cref="ProcessTimeoutDecision.NotApplicable"/>.</returns>
    public virtual ProcessTimeoutDecision DecideTimeout(ProcessTimeout timeout, DateTime utcNow) =>
        ProcessTimeoutDecision.NotApplicable();
}
