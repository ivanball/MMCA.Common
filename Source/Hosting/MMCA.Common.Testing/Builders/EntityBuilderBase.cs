namespace MMCA.Common.Testing.Builders;

/// <summary>
/// Base class for fluent entity builders used in tests. Subclasses configure
/// sensible defaults so tests only specify the properties they care about.
/// </summary>
/// <typeparam name="TBuilder">The concrete builder type (for fluent chaining).</typeparam>
/// <typeparam name="TEntity">The entity type being built.</typeparam>
public abstract class EntityBuilderBase<TBuilder, TEntity>
    where TBuilder : EntityBuilderBase<TBuilder, TEntity>
{
    /// <summary>
    /// Builds the entity using the configured values. Throws if the domain
    /// factory method returns a failure result.
    /// </summary>
    /// <returns>The fully constructed entity.</returns>
    public abstract TEntity Build();
}
