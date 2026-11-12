using MMCA.Common.Application.InternalCommands;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// A dedicated delivery lane over the internal-command queue: the rows of one command type that a
/// separate, sequential <see cref="InternalCommandLaneProcessor"/> drains instead of the shared
/// <see cref="InternalCommandProcessor"/>, keyed by receiver so a failing receiver can be cooled down.
/// </summary>
/// <param name="Name">The lane name, emitted as the <c>lane</c> metric tag and log value.</param>
/// <param name="CommandType">The stored command name the lane claims.</param>
/// <param name="ReceiverCooldown">
/// The configured cooldown after a failed real attempt; the lane clamps it to the shortest retry span
/// (<see cref="InternalCommandLaneProcessor.EffectiveCooldown"/>).
/// </param>
/// <param name="ReceiverKey">Reads the receiver key of a deserialized command; null means no key.</param>
internal sealed record InternalCommandLane(
    string Name,
    string CommandType,
    TimeSpan ReceiverCooldown,
    Func<IInternalCommand, string?> ReceiverKey);
