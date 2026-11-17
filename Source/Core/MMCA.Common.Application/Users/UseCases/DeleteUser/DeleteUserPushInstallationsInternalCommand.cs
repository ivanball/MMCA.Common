using MMCA.Common.Application.InternalCommands;

namespace MMCA.Common.Application.Users.UseCases.DeleteUser;

/// <summary>
/// Deletes every native-push installation an erased account owns (ADR-044, ADR-114), so a device
/// still signed in elsewhere stops receiving pushes addressed to it.
/// </summary>
/// <remarks>
/// <see cref="DeleteUserHandlerBase{TUser, TCommand}"/> schedules it inside the erasure's unit of
/// work when the subclass forwards an <see cref="IInternalCommandScheduler"/>, so the row commits with
/// the anonymized account or rolls back with it, and the processor retries it until the push
/// provider answers. <c>AddApplication()</c> registers its handler, so a host needs no registration
/// of its own. The payload is the user id alone, which round-trips through JSON as the queue requires.
/// </remarks>
/// <param name="UserId">The erased account.</param>
[InternalCommandName("MMCA.Users.DeletePushInstallations.v1")]
public sealed record DeleteUserPushInstallationsInternalCommand(UserIdentifierType UserId) : IInternalCommand;
