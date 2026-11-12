using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// What <see cref="ProcessTimeoutHandlerBase{TCommand, TProcess, TProcessId}"/> did with one fired
/// timeout, handed to its <c>OnHandled</c> hook for logging.
/// </summary>
/// <typeparam name="TProcessId">The process aggregate's identifier type.</typeparam>
/// <param name="Handling">The handling outcome.</param>
/// <param name="ProcessId">The id of the process the timeout fired on.</param>
/// <param name="TimeoutName">The name of the resolved timeout.</param>
/// <param name="DueAt">The due instant, for <see cref="ProcessTimeoutHandling.NotYetDue"/> only.</param>
/// <param name="Errors">The aggregate's reasons, for <see cref="ProcessTimeoutHandling.Rejected"/> only.</param>
/// <param name="StateBefore">The process state described BEFORE the aggregate decided, or <see langword="null"/>.</param>
public sealed record ProcessTimeoutReport<TProcessId>(
    ProcessTimeoutHandling Handling,
    TProcessId ProcessId,
    string TimeoutName,
    DateTime? DueAt,
    IReadOnlyList<Error> Errors,
    string? StateBefore)
    where TProcessId : notnull;
