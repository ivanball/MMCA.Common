using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Users.UseCases.DeleteUser;

/// <summary>
/// Runs <see cref="DeleteUserPushInstallationsInternalCommand"/>: deletes the erased account's
/// installations through the host's <see cref="IPushDeviceRegistrar"/>.
/// </summary>
/// <remarks>
/// Idempotent, as at-least-once execution requires: installations already deleted are no longer
/// found. A failure is returned unchanged, which leaves the row for the processor's backoff, since a
/// push provider outage is exactly what the durable queue exists to survive. On a host with no push
/// adapter the registrar is the inert default and the command completes having deleted nothing, so
/// the command must run on the host that registers the adapter.
/// </remarks>
/// <param name="registrar">The host's device registrar.</param>
/// <param name="logger">Receives one line per outcome, carrying the user id only.</param>
internal sealed partial class DeleteUserPushInstallationsInternalCommandHandler(
    IPushDeviceRegistrar registrar,
    ILogger<DeleteUserPushInstallationsInternalCommandHandler> logger)
    : ICommandHandler<DeleteUserPushInstallationsInternalCommand, Result>
{
    /// <inheritdoc />
    public async Task<Result> HandleAsync(DeleteUserPushInstallationsInternalCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var result = await registrar.DeleteAllForUserAsync(command.UserId, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            LogInstallationsDeleted(logger, command.UserId);
        }
        else
        {
            LogInstallationsDeleteFailed(logger, command.UserId);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Native-push installations of erased user {UserId} deleted")]
    private static partial void LogInstallationsDeleted(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native-push installations of erased user {UserId} could not be deleted; the internal command will retry")]
    private static partial void LogInstallationsDeleteFailed(ILogger logger, UserIdentifierType userId);
}
