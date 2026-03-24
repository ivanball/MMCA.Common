namespace MMCA.Common.Application.Services.Filtering;

/// <summary>
/// Defines a strategy for applying query filters against a specific property type.
/// </summary>
public interface IFilterStrategy
{
    /// <summary>
    /// Applies a filter expression to the query using LINQ Dynamic.
    /// </summary>
    /// <typeparam name="T">The entity type being queried.</typeparam>
    /// <param name="query">The queryable to filter.</param>
    /// <param name="property">The entity property name or path to filter on.</param>
    /// <param name="op">The operator (e.g. "EQUALS", "CONTAINS"), already uppercased by the caller.</param>
    /// <param name="value">The filter value as a string, parsed by each strategy.</param>
    /// <returns>The filtered queryable, or the original query if the operator is unrecognized.</returns>
    IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value);

    /// <summary>
    /// The set of operator names this strategy supports (e.g., "EQUALS", "CONTAINS").
    /// Returns null by default, meaning operator validation is skipped for custom strategies.
    /// Built-in strategies override this to enable upstream validation.
    /// </summary>
    IReadOnlySet<string>? SupportedOperators => null;
}
