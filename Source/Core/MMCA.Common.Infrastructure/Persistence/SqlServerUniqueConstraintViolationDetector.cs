using Microsoft.Data.SqlClient;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of <see cref="IUniqueConstraintViolationDetector"/>: walks the
/// inner-exception chain and answers from the provider's own error numbers rather than from the
/// wording of a message.
/// <para>
/// EF Core surfaces a rejected insert as a <c>DbUpdateException</c> wrapping the provider's
/// <see cref="SqlException"/>, so the chain is walked rather than the outermost exception alone.
/// The two numbers that matter are 2601 (duplicate key rejected by a unique INDEX) and 2627
/// (duplicate key violating a PRIMARY KEY or UNIQUE constraint); every other SQL Server error (a
/// foreign-key failure, a deadlock victim, a timeout) is deliberately NOT a unique violation and
/// must keep propagating, because treating those as a collision would either loop or hide a real
/// fault.
/// </para>
/// <para>
/// This is also the default registration for hosts running another engine. The number check simply
/// never matches there, and the message fallback below carries them: SQLite reports
/// <c>UNIQUE constraint failed</c>, PostgreSQL <c>duplicate key value violates unique constraint</c>,
/// and the fallback recognises both. A dedicated implementation can replace this one whenever an
/// engine deserves its own number check.
/// </para>
/// <para>
/// Stateless by construction: it holds nothing between calls and reads only the exception handed
/// to it, which is what lets the container register it as a singleton.
/// </para>
/// </summary>
public sealed class SqlServerUniqueConstraintViolationDetector : IUniqueConstraintViolationDetector
{
    /// <summary>SQL Server error 2601: duplicate key rejected by a unique INDEX.</summary>
    private const int UniqueIndexViolation = 2601;

    /// <summary>SQL Server error 2627: duplicate key violating a PRIMARY KEY or UNIQUE constraint.</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>The wording SQL Server and PostgreSQL share, matched only by the fallback below.</summary>
    private const string DuplicateKeyText = "duplicate key";

    /// <summary>The wording SQLite uses for the same rejection.</summary>
    private const string UniqueConstraintFailedText = "UNIQUE constraint failed";

    /// <inheritdoc />
    public bool IsUniqueConstraintViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Number is UniqueIndexViolation or UniqueConstraintViolation)
            {
                return true;
            }

            // Fallback, and ONLY a fallback: it exists so a link in the chain that is not a
            // SqlException still classifies. A wrapper that captured the provider failure as text
            // (a retry decorator re-throwing its own type, another engine's provider, a test double
            // standing in for the provider) carries the number nowhere but the message, and both
            // 2601 and 2627 report "Cannot insert duplicate key". The numbers themselves are never
            // matched as text: they do not appear in the message, so searching for them would match
            // nothing real while happily matching an unrelated exception that quoted those digits.
            if (current.Message.Contains(DuplicateKeyText, StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains(UniqueConstraintFailedText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
