using MMCA.Common.Application.InternalCommands;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// A dedicated delivery lane over the internal-command queue: the rows of one command type that a
/// separate <see cref="InternalCommandLaneProcessor"/> drains instead of the shared
/// <see cref="InternalCommandProcessor"/>, keyed by receiver so a failing receiver can be cooled down and
/// two rows of one receiver never run at once.
/// </summary>
/// <param name="Name">The lane name, emitted as the <c>lane</c> metric tag and log value.</param>
/// <param name="CommandType">The stored command name the lane claims.</param>
/// <param name="ReceiverCooldown">
/// The configured cooldown after a failed real attempt; the lane clamps it so one attempt in flight still
/// fits inside the shortest retry span (<see cref="InternalCommandLaneProcessor.EffectiveCooldown"/>).
/// </param>
/// <param name="ReceiverKey">Reads the receiver key of a deserialized command; null means no key.</param>
/// <param name="MaxConcurrency">How many rows of one claimed batch run at once on this replica; 1 keeps one row at a time.</param>
/// <param name="BatchStartBudget">
/// How long after a batch's first start new rows may still start; the rows that never started are released.
/// Also the longest one attempt can take, which the cooldown clamp leaves room for.
/// </param>
internal sealed record InternalCommandLane(
    string Name,
    string CommandType,
    TimeSpan ReceiverCooldown,
    Func<IInternalCommand, string?> ReceiverKey,
    int MaxConcurrency,
    TimeSpan BatchStartBudget);
