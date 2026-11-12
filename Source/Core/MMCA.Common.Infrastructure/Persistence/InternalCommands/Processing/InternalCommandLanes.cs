namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// The lane registry the shared <see cref="InternalCommandProcessor"/> reads once, on its first cycle:
/// when it is registered, the shared fetch and count exclude <see cref="DedicatedCommandType"/>, whose
/// rows a dedicated lane drains. It is registered only together with that lane, so a host without it
/// polls exactly the pending predicate.
/// </summary>
/// <param name="dedicatedCommandType">The stored command name a dedicated lane owns.</param>
internal sealed class InternalCommandLanes(string dedicatedCommandType)
{
    /// <summary>Gets the stored command name the shared loop excludes.</summary>
    public string DedicatedCommandType { get; } = dedicatedCommandType;
}
