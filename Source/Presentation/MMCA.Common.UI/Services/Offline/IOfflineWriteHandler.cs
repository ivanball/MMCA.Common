using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Offline;

/// <summary>
/// Replays one kind of queued write. An app registers one per kind with
/// <c>AddOfflineWriteHandler&lt;THandler&gt;()</c>; the queue picks it by an ordinal match on <see cref="Kind"/>.
/// </summary>
public interface IOfflineWriteHandler
{
    /// <summary>Gets the kind of write this handler replays, for example <c>Engagement.Bookmark.Create.v1</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// Sends the write, with its <see cref="PendingWrite.IdempotencyKey"/> (and <see cref="PendingWrite.IfMatch"/>
    /// when set), and returns the server's answer as a <see cref="Result"/>. A transport failure, a
    /// timeout, an unexpected, too-many-requests or unauthorized error keeps the write for a later
    /// replay; a conflict or any other failure discards it.
    /// </summary>
    /// <param name="write">The write to replay.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>The replay's result.</returns>
    Task<Result> ReplayAsync(PendingWrite write, CancellationToken cancellationToken);
}
