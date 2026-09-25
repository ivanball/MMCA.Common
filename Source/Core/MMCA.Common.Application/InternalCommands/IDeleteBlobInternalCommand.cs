namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// A durable request to delete one blob that a committed change left orphaned (ADR-045 storage,
/// executed through the internal-command queue).
/// </summary>
/// <remarks>
/// <para>
/// A path that replaces or removes a stored file commits a row that no longer points at the blob
/// and then has to delete it. Doing that inline makes the delete best effort: a storage outage, a
/// deploy or a crash in the post-commit tail leaks the blob with nothing left that knows about it.
/// Scheduling one of these on the same unit of work makes the delete survive all three, and the
/// framework retries it.
/// </para>
/// <para>
/// Implement it as a small record carrying only the name, declare an
/// <see cref="InternalCommandNameAttribute"/> so a later rename cannot strand rows in flight, and
/// handle it with a sealed subclass of <see cref="DeleteBlobInternalCommandHandlerBase{TCommand}"/>.
/// </para>
/// </remarks>
public interface IDeleteBlobInternalCommand : IInternalCommand
{
    /// <summary>Gets the name of the blob to delete.</summary>
    string BlobName { get; }
}
