namespace MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

/// <summary>
/// Decides whether a failed save was rejected because another writer had already moved the row's
/// concurrency token, so an application handler that claims work by writing a row can recognise
/// losing that claim without knowing which persistence provider is underneath it.
/// </summary>
/// <remarks>
/// <para>
/// The same reasoning as <see cref="IUniqueConstraintViolationDetector"/> beside it: classifying a
/// provider error is an infrastructure concern. The error identity lives in a provider type
/// (<c>Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException</c>), and the Application layer
/// references <c>MMCA.Common.Domain</c> and no data provider at all, so the question is declared
/// here and answered in Infrastructure where those types are already referenced. Catching the
/// provider exception in a handler is what that constraint used to force, and it drags provider
/// vocabulary into a layer whose whole purpose is to stay persistence neutral.
/// </para>
/// <para>
/// A miss is safe rather than silent: an unclassified exception propagates, and the caller sees
/// exactly the failure it would have seen with no detection in the code at all.
/// </para>
/// </remarks>
public interface IConcurrencyConflictDetector
{
    /// <summary>
    /// Determines whether <paramref name="exception"/>, or anything in its inner-exception chain,
    /// reports an optimistic-concurrency conflict.
    /// </summary>
    /// <param name="exception">The exception raised by the failed save.</param>
    /// <returns>
    /// <see langword="true"/> when the chain describes a concurrency conflict; otherwise
    /// <see langword="false"/>.
    /// </returns>
    bool IsConcurrencyConflict(Exception exception);
}
