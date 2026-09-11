namespace MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

/// <summary>
/// Abstracts the one thing LINQ cannot express: a hand-written SQL statement, run without a direct
/// dependency on Entity Framework Core in the Application layer. The sibling of
/// <see cref="IQueryableExecutor"/>, for reads that need a window function, a recursive CTE or a
/// vendor-specific operator.
/// <para>
/// <b>Parameterized by construction.</b> Both entry points take a
/// <see cref="FormattableString"/> and nothing else, so the only way to reach this interface is an
/// interpolated string literal whose holes the provider turns into command parameters. A caller
/// cannot hand it a concatenated string: <c>"SELECT ... WHERE Name = '" + name + "'"</c> is a
/// <see cref="string"/> and does not compile against either method. Injection is therefore not a
/// review item on this path, it is a compile error, and the statement text stays stable across calls
/// so the server can reuse its plan.
/// </para>
/// <para>
/// <b>Relational only.</b> The implementation targets the host's default physical data source. A
/// host whose default source is Cosmos DB gets a <see cref="NotSupportedException"/> naming the
/// engine: Cosmos speaks its own query language and has no SQL command surface to parameterize.
/// </para>
/// </summary>
public interface IRawSqlQueryExecutor
{
    /// <summary>Runs an interpolated SQL query and materializes every row.</summary>
    /// <typeparam name="T">The row shape: a scalar (a number, a text value, a timestamp) or an unmapped DTO whose properties match the selected columns by name.</typeparam>
    /// <param name="sql">The interpolated SQL statement; every hole becomes a command parameter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The materialized rows, empty when the statement selects none.</returns>
    /// <exception cref="NotSupportedException">The host's default data source is not a relational engine.</exception>
    Task<IReadOnlyList<T>> QueryAsync<T>(FormattableString sql, CancellationToken cancellationToken = default);

    /// <summary>Runs an interpolated SQL query expected to select at most one row.</summary>
    /// <typeparam name="T">The row shape: a scalar or an unmapped DTO whose properties match the selected columns by name.</typeparam>
    /// <param name="sql">The interpolated SQL statement; every hole becomes a command parameter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The single row, or the default of <typeparamref name="T"/> when the statement selects none.</returns>
    /// <exception cref="InvalidOperationException">The statement selected more than one row.</exception>
    /// <exception cref="NotSupportedException">The host's default data source is not a relational engine.</exception>
    Task<T?> QuerySingleOrDefaultAsync<T>(FormattableString sql, CancellationToken cancellationToken = default);
}
