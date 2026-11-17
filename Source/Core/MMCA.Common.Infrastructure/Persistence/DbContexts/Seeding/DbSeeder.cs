using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;

/// <summary>
/// Base class for module-specific database seeders. Provides a helper to convert integer seed IDs
/// to the module's identifier type, enabling seeders to work consistently across int and Guid key strategies,
/// and the save that tolerates a concurrent replica seeding the same rows.
/// </summary>
public abstract class DbSeeder : IDbSeeder
{
    /// <inheritdoc />
    public abstract Task SeedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Converts an integer seed ID to the target identifier type. Supports <see cref="int"/> (pass-through)
    /// and <see cref="Guid"/> (deterministic conversion using the int's bytes padded to 16 bytes).
    /// </summary>
    /// <typeparam name="TIdentifier">The target identifier type.</typeparam>
    /// <param name="id">The integer seed value.</param>
    /// <returns>The converted identifier.</returns>
    /// <exception cref="NotSupportedException">Thrown for unsupported identifier types.</exception>
    protected static TIdentifier GetId<TIdentifier>(int id)
        where TIdentifier : notnull
    {
        if (typeof(TIdentifier) == typeof(Guid))
        {
            // Deterministic Guid: zeroed 16-byte span with the int written at the start.
            // Produces reproducible Guids from seed integers for consistent test/seed data.
            Span<byte> bytes = stackalloc byte[16];
            BitConverter.TryWriteBytes(bytes, id);
            var guid = new Guid(bytes);
            return (TIdentifier)(object)guid;
        }

        if (typeof(TIdentifier) == typeof(int))
        {
            return (TIdentifier)(object)id;
        }

        throw new NotSupportedException($"Identifier type '{typeof(TIdentifier)}' is not supported.");
    }

    /// <summary>
    /// Saves the pending seed rows, treating a unique or primary-key violation as a lost race against
    /// a concurrent replica seeding the same rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Seeding is check-then-insert, so two replicas starting together on a database that lacks a row
    /// both pass the existence check and both insert; the slower save is rejected by the unique index
    /// or the primary key. That rejection is not a fault: the winner wrote the very rows this run meant
    /// to write. On such a violation every pending entry of each context the failed save touched is
    /// detached (a failed save rolls back as a whole, so everything still pending there belongs to it;
    /// left tracked, the scoped context would replay the same rejected insert on the next save and fail
    /// again), and the method returns <see langword="false"/> so the caller can log the skip and carry
    /// on. Every other save failure propagates.
    /// </para>
    /// <para>
    /// The violation is classified by the unit of work's registered
    /// <see cref="IUniqueConstraintViolationDetector"/> when it exposes one (the host's engine-aware
    /// choice), else by <see cref="SqlServerUniqueConstraintViolationDetector"/>.
    /// </para>
    /// </remarks>
    /// <param name="unitOfWork">The unit of work holding the pending seed rows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when the rows were saved; <see langword="false"/> when a concurrent
    /// replica had already inserted them and the failed save was discarded.
    /// </returns>
    protected static async Task<bool> SaveToleratingConcurrentSeedAsync(
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsUniqueConstraintViolation(unitOfWork, exception))
        {
            // Lost the insert race to a concurrent replica seeding the same rows: its rows are the
            // ones this run meant to write, so discard the failed save and let the caller move on.
            DiscardFailedSave(exception);
            return false;
        }
    }

    // The fallback is built on demand rather than cached: this runs only on a failed save.
    private static bool IsUniqueConstraintViolation(IUnitOfWork unitOfWork, Exception exception) =>
        (unitOfWork as IUniqueConstraintViolationDetector ?? new SqlServerUniqueConstraintViolationDetector())
            .IsUniqueConstraintViolation(exception);

    /// <summary>
    /// Detaches every pending entry of each context the failed save touched. A failed save rolls
    /// back as a whole, so everything still pending there belongs to it (the rejected rows and any
    /// rows captured with them); left tracked, the scoped context would replay the same rejected
    /// insert on the next save and fail again.
    /// </summary>
    private static void DiscardFailedSave(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not DbUpdateException updateException)
            {
                continue;
            }

            foreach (var context in updateException.Entries.Select(entry => entry.Context).Distinct())
            {
                var pending = context.ChangeTracker.Entries()
                    .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    .ToList();

                foreach (var entry in pending)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return;
        }
    }
}
