using MMCA.Common.Application.InternalCommands;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// A consumer-owned internal command that fires a named timeout on one process aggregate. Declare a
/// stable <see cref="InternalCommandNameAttribute"/> on every implementation, and handle it with a
/// subclass of <see cref="ProcessTimeoutHandlerBase{TCommand, TProcess, TProcessId}"/>.
/// </summary>
/// <remarks>
/// <see cref="ProcessId"/> may be an explicit interface implementation over the command's own id
/// property. <c>System.Text.Json</c> does not serialize an explicit implementation, so the stored
/// payload is the command's own public properties only and an existing command adopts this interface
/// without changing what its rows carry.
/// </remarks>
/// <typeparam name="TProcessId">The process aggregate's identifier type.</typeparam>
public interface IProcessTimeoutCommand<TProcessId> : IInternalCommand
    where TProcessId : notnull
{
    /// <summary>Gets the id of the process the timeout fires on.</summary>
    TProcessId ProcessId { get; }
}
