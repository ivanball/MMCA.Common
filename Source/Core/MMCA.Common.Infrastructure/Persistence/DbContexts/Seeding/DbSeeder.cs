namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;

/// <summary>
/// Base class for module-specific database seeders. Provides a helper to convert integer seed IDs
/// to the module's identifier type, enabling seeders to work consistently across int and Guid key strategies.
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
}
