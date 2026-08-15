namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;

/// <summary>
/// Thrown when the commit phase of <see cref="DbContextFactory.ExecuteInTransactionAsync{TResult}"/>
/// fails, leaving the outcome <b>ambiguous</b>: the transaction may or may not have become durable,
/// because a commit can fail after the database applied it but before the acknowledgement reached
/// the client.
/// <para>
/// The wrapper exists to take the failure out of the retry path. SQL Server's
/// <c>EnableRetryOnFailure</c> strategy classifies most commit-phase errors (timeouts, dropped
/// connections) as transient, and a retry re-runs the whole operation. Against a commit that may
/// already be durable, that duplicates every write the operation performed, including its outbox
/// rows. Reporting the ambiguity is the only safe outcome.
/// </para>
/// <para>
/// Recovery belongs to the caller. An API request marked <c>[Idempotent]</c> replays safely, and
/// whatever the transaction wrote to the outbox is delivered by the outbox processor if the commit
/// did land. In-process domain event dispatch deferred by the transaction is dropped, so no handler
/// acts on state that may not exist.
/// </para>
/// </summary>
public sealed class TransactionCommitAmbiguousException : Exception
{
    private const string DefaultMessage =
        "The transaction commit failed with an unknown outcome: it may or may not have been made durable. "
        + "The operation was deliberately not retried, because retrying a possibly-durable commit duplicates its writes.";

    /// <summary>Initializes a new instance with the default ambiguity message.</summary>
    public TransactionCommitAmbiguousException()
        : base(DefaultMessage) { }

    /// <summary>Initializes a new instance with a custom message.</summary>
    /// <param name="message">The exception message.</param>
    public TransactionCommitAmbiguousException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance with a custom message and the failure that caused the ambiguity.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The commit failure reported by the provider.</param>
    public TransactionCommitAmbiguousException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>Initializes a new instance carrying <paramref name="innerException"/> under the default message.</summary>
    /// <param name="innerException">The commit failure reported by the provider.</param>
    public TransactionCommitAmbiguousException(Exception innerException)
        : base(DefaultMessage, innerException) { }

    /// <summary>
    /// Initializes a new instance carrying <paramref name="innerException"/> plus the outcome of
    /// every physical source the failed commit touched. The message is the default ambiguity text
    /// followed by a per-source outcome sentence; groups with no sources are left out of it.
    /// </summary>
    /// <param name="innerException">The commit failure reported by the provider.</param>
    /// <param name="committedSources">The sources whose commit had already succeeded.</param>
    /// <param name="ambiguousSource">The source whose commit threw, or <see langword="null"/> when none is known.</param>
    /// <param name="rolledBackSources">The sources still uncommitted when the failure hit.</param>
    public TransactionCommitAmbiguousException(
        Exception innerException,
        IReadOnlyList<string> committedSources,
        string? ambiguousSource,
        IReadOnlyList<string> rolledBackSources)
        : base(
            ComposeMessage(committedSources ?? [], ambiguousSource, rolledBackSources ?? []),
            innerException)
    {
        CommittedSources = committedSources ?? [];
        AmbiguousSource = ambiguousSource;
        RolledBackSources = rolledBackSources ?? [];
    }

    /// <summary>
    /// The physical sources whose commit succeeded before the failure. Commits are sequential and
    /// independent (no two-phase commit), so these are durable: the ambiguity is a PARTIAL commit,
    /// and reconciling it means replaying only what the remaining sources owed.
    /// </summary>
    public IReadOnlyList<string> CommittedSources { get; } = [];

    /// <summary>
    /// The physical source whose commit threw, or <see langword="null"/> when the failure could not
    /// be attributed to one source. Its outcome alone is unknowable: the database may have applied
    /// the transaction and lost only the acknowledgement. With a single transactional source, the
    /// case every host runs today, this source IS the whole outcome and the other two groups are
    /// empty.
    /// </summary>
    public string? AmbiguousSource { get; }

    /// <summary>
    /// The physical sources still uncommitted when the failure hit, rolled back on a best-effort
    /// basis. Best-effort is literal: a rollback that itself throws (a zombied transaction, a lost
    /// connection) is swallowed and the transaction abandoned to the server, which ends it when the
    /// connection is reclaimed. A source listed here therefore wrote nothing that survives.
    /// </summary>
    public IReadOnlyList<string> RolledBackSources { get; } = [];

    /// <summary>
    /// Appends the per-source outcome sentence to the default ambiguity message, skipping any group
    /// with nothing in it so a single-source failure reads as one clause rather than three.
    /// </summary>
    private static string ComposeMessage(
        IReadOnlyList<string> committedSources,
        string? ambiguousSource,
        IReadOnlyList<string> rolledBackSources)
    {
        var groups = new List<string>(3);

        if (committedSources.Count > 0)
            groups.Add("committed [" + string.Join(", ", committedSources) + "]");

        if (!string.IsNullOrWhiteSpace(ambiguousSource))
            groups.Add("ambiguous [" + ambiguousSource + "]");

        if (rolledBackSources.Count > 0)
            groups.Add("rolled back [" + string.Join(", ", rolledBackSources) + "]");

        return groups.Count == 0
            ? DefaultMessage
            : DefaultMessage + " Per-source outcome: " + string.Join("; ", groups) + ".";
    }
}
