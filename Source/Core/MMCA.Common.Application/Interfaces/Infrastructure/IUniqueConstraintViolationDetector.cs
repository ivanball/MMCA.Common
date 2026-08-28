namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Decides whether a failed save was rejected for violating a unique constraint, so an application
/// handler whose pre-check can lose a race against a concurrent insert can recognise the collision
/// and answer with the same conflict the pre-check would have produced, without knowing which
/// persistence provider is underneath it.
/// </summary>
/// <remarks>
/// <para>
/// Classifying a provider error is an infrastructure concern, not an application one. The error
/// identity lives in a provider type: SQL Server reports the collision as
/// <c>SqlException.Number</c> 2601 (unique index) or 2627 (primary key or unique constraint), and
/// neither that type nor the EF Core <c>DbUpdateException</c> that wraps it is reachable from the
/// Application layer, which references <c>MMCA.Common.Domain</c> and no data provider at all.
/// Reading the exception MESSAGE from a handler is what that constraint used to force, and it is
/// the wrong answer twice over: the wording is a provider and locale detail that can change under a
/// working handler, and matching on it drags provider vocabulary into a layer whose whole purpose is
/// to stay persistence neutral.
/// </para>
/// <para>
/// So the question is declared here and answered in Infrastructure, where the provider types are
/// already referenced. Swapping engines swaps the implementation, and every handler that recovers
/// from a lost insert race keeps working unchanged.
/// </para>
/// <para>
/// A miss is safe rather than silent: an unclassified exception simply propagates, and the caller
/// sees exactly the failure it would have seen with no detection in the code at all.
/// </para>
/// </remarks>
public interface IUniqueConstraintViolationDetector
{
    /// <summary>
    /// Determines whether <paramref name="exception"/>, or anything in its inner-exception chain,
    /// reports a unique-constraint violation.
    /// </summary>
    /// <param name="exception">The exception raised by the failed save.</param>
    /// <returns>
    /// <see langword="true"/> when the chain describes a unique-constraint violation;
    /// otherwise <see langword="false"/>.
    /// </returns>
    bool IsUniqueConstraintViolation(Exception exception);
}
