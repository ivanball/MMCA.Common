using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands;

/// <summary>
/// The processor wake owed by internal-command rows enrolled in an open transaction. The scheduler
/// cannot signal at enrollment (the row is not visible until the commit), so it records the owed wake
/// against the context here, and the unit of work that owns the transaction releases it once, after
/// the commit succeeds, or drops it on rollback. Keyed weakly on the context like the deferred
/// domain-event dispatch, so a context nobody commits leaves nothing behind.
/// </summary>
internal static class EnrolledCommandWake
{
    private static readonly ConditionalWeakTable<DbContext, IInternalCommandSignal> Owed = [];

    /// <summary>Records that <paramref name="context"/>'s open transaction holds an enrolled row.</summary>
    /// <param name="context">The context the row was enrolled on.</param>
    /// <param name="signal">The processor wake to release after the commit.</param>
    internal static void Defer(DbContext context, IInternalCommandSignal signal) =>
        Owed.AddOrUpdate(context, signal);

    /// <summary>
    /// Releases the wakes owed by <paramref name="committedContexts"/>, once per distinct signal no
    /// matter how many rows or contexts enrolled, then forgets them. Call only after every commit
    /// succeeded.
    /// </summary>
    /// <param name="committedContexts">The contexts whose transactions just committed.</param>
    internal static void Release(IEnumerable<DbContext> committedContexts)
    {
        var signals = new List<IInternalCommandSignal>();
        foreach (var context in committedContexts)
        {
            if (Owed.TryGetValue(context, out var signal))
            {
                Owed.Remove(context);
                if (!signals.Contains(signal))
                {
                    signals.Add(signal);
                }
            }
        }

        foreach (var signal in signals)
        {
            signal.Signal();
        }
    }

    /// <summary>Forgets the wake owed by <paramref name="context"/>: its transaction did not commit.</summary>
    /// <param name="context">The context whose transaction rolled back or was abandoned.</param>
    internal static void Drop(DbContext context) => Owed.Remove(context);
}
