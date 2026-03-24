using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace MMCA.Common.Infrastructure.Persistence.ValueGenerators;

/// <summary>
/// Generates unique int IDs for entities stored in Cosmos DB, which does not support
/// server-side identity columns. Uses a process-level counter seeded from the current
/// Unix timestamp to minimise collisions across application restarts.
/// </summary>
/// <remarks>
/// The seed wraps around <see cref="int.MaxValue"/> via modulo to avoid overflow.
/// Interlocked.Increment ensures thread-safe ID generation without locks.
/// Collisions are possible across process instances; for truly unique IDs consider GUIDs.
/// </remarks>
public sealed class CosmosIntIdValueGenerator : ValueGenerator<int>
{
    private static int _seed = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % int.MaxValue);

    /// <inheritdoc />
    public override bool GeneratesTemporaryValues => false;

    /// <inheritdoc />
    public override int Next(EntityEntry entry)
        => Interlocked.Increment(ref _seed);
}
