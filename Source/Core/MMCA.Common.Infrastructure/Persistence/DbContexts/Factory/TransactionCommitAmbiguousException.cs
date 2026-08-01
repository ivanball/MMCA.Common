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
}
