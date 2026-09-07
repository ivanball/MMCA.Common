using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// EF Core answer to <see cref="IConcurrencyConflictDetector"/>. Every
/// <c>AuditableBaseEntity</c> carries a <c>RowVersion</c> token, so a save whose UPDATE matched no
/// row because another writer got there first surfaces as
/// <see cref="DbUpdateConcurrencyException"/>.
/// </summary>
/// <remarks>
/// Stateless by construction: it holds nothing between calls and reads only the exception handed to
/// it, which is what lets the container register it as a singleton. The chain is walked rather than
/// the outermost exception alone, because a save wrapped by a transaction or a retrying execution
/// strategy carries the real cause underneath.
/// </remarks>
public sealed class EfCoreConcurrencyConflictDetector : IConcurrencyConflictDetector
{
    /// <inheritdoc />
    public bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException)
            {
                return true;
            }
        }

        return false;
    }
}
