using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// Deletes the blob an <see cref="IDeleteBlobInternalCommand"/> names, idempotently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent, as at-least-once execution requires.</b> A blob that is already gone is the state
/// the command asks for, so a <see cref="ErrorType.NotFound"/> answer completes the row with
/// <see cref="Result.Success()"/> instead of retrying forever. Any other failure is returned
/// unchanged, which leaves the row for the processor's backoff: a storage outage is exactly the case
/// the durable queue exists to survive.
/// </para>
/// <para>
/// A consumer derives one sealed class per command so the handler scan registers a closed
/// <c>ICommandHandler&lt;TCommand, Result&gt;</c> the decorator pipeline wraps, and overrides
/// <see cref="BlobKind"/> to name the blob in its log lines.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The app's delete-blob internal command.</typeparam>
/// <param name="fileStorage">The blob store.</param>
/// <param name="logger">Receives one line per deletion outcome.</param>
public abstract partial class DeleteBlobInternalCommandHandlerBase<TCommand>(
    IFileStorageService fileStorage,
    ILogger logger)
    : ICommandHandler<TCommand, Result>
    where TCommand : IDeleteBlobInternalCommand
{
    /// <summary>
    /// Gets the label the log lines use for the blob (for example <c>"Avatar blob"</c>). Defaults to
    /// <c>"Blob"</c>.
    /// </summary>
    protected virtual string BlobKind => "Blob";

    /// <inheritdoc />
    public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var result = await fileStorage.DeleteAsync(command.BlobName, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            LogBlobDeleted(logger, BlobKind, command.BlobName);
            return result;
        }

        if (result.Errors.Any(e => e.Type == ErrorType.NotFound))
        {
            LogBlobAlreadyGone(logger, BlobKind, command.BlobName);
            return Result.Success();
        }

        LogBlobDeleteFailed(
            logger,
            BlobKind,
            command.BlobName,
            result.Errors is [var first, ..] ? first.Message : "Unknown error");
        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{BlobKind} {BlobName} deleted")]
    private static partial void LogBlobDeleted(ILogger logger, string blobKind, string blobName);

    [LoggerMessage(Level = LogLevel.Information, Message = "{BlobKind} {BlobName} was already gone; nothing to delete")]
    private static partial void LogBlobAlreadyGone(ILogger logger, string blobKind, string blobName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{BlobKind} {BlobName} could not be deleted: {Reason}. The row will be retried.")]
    private static partial void LogBlobDeleteFailed(ILogger logger, string blobKind, string blobName, string reason);
}
