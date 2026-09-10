using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// Marks a command that is <b>scheduled</b> for later execution rather than dispatched inline.
/// Scheduling writes one row to the host's <c>InternalCommands</c> table through
/// <see cref="IInternalCommandScheduler"/>; a background processor claims that row when it comes
/// due and executes it through the ordinary CQRS pipeline.
/// <para>
/// There is deliberately no second handler contract. An internal command is handled by the same
/// <see cref="ICommandHandler{TCommand, TResult}"/> a synchronous command uses, so every decorator
/// (feature gate, authorization, logging, cache invalidation, validation, timeout, transaction)
/// applies to a deferred execution exactly as it does to an inline one.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload must round-trip through JSON.</b> The row stores
/// <c>System.Text.Json</c> output and the processor deserializes it against the resolved command
/// type, so a command carrying only serializable members is a requirement, not a style preference.
/// Keep them small: identifiers and scalars, never loaded aggregates.
/// </para>
/// <para>
/// <b>Execution is at-least-once.</b> A claim lease stops two replicas from running the same row at
/// the same time, but a host that dies after the handler committed and before the row was stamped
/// re-runs that command once its lease expires. Handlers must be idempotent.
/// </para>
/// <para>
/// <b>Renames are visible, not silent.</b> The row stores the command's assembly-qualified name, or
/// the identity declared by <see cref="InternalCommandNameAttribute"/> when it has one. A rename
/// without the attribute leaves rows already in flight unresolvable, and the processor logs that and
/// dead-letters them rather than dropping them quietly. Declare the attribute on any command whose
/// type name may move.
/// </para>
/// </remarks>
public interface IInternalCommand : ICommand<Result>;
