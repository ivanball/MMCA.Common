using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Infrastructure.Persistence.DataSources.Engines;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Builds the <c>IsDeleted = 0</c> index predicate for a given engine. Shared by
/// <see cref="Conventions.SoftDeleteUniqueIndexConvention"/> (which applies it automatically to
/// unique indexes) and by
/// <see cref="Configuration.IndexBuilderExtensions.HasSoftDeleteFilter(Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder, DataSource, string?)"/>
/// (which a hand-authored non-unique index opts into), so the two never disagree about identifier
/// quoting or about which column carries the soft-delete flag.
/// </summary>
internal static class SoftDeleteFilterSql
{
    /// <summary>
    /// Builds the filter predicate for the soft-delete flag of <paramref name="entityType"/>.
    /// </summary>
    /// <param name="engine">The engine of the model being built (identifier quoting differs per provider).</param>
    /// <param name="entityType">The entity type owning the index.</param>
    /// <returns>
    /// The predicate SQL, or <see langword="null"/> for an engine with no filtered-index support
    /// (Cosmos), where the caller must leave the index untouched.
    /// </returns>
    /// <remarks>
    /// The predicate itself is the engine's (<see cref="IDataSourceEngine.BuildSoftDeleteFilter"/>).
    /// PostgreSQL maps the flag to a real boolean column and refuses to compare one with an integer,
    /// so it answers <c>= false</c> where every other relational engine answers <c>= 0</c>; that is
    /// the only place the soft-delete filter differs by engine beyond quoting.
    /// </remarks>
    internal static string? Build(DataSource engine, IReadOnlyEntityType entityType) =>
        DataSourceEngines.For(engine).BuildSoftDeleteFilter(ColumnName(entityType));

    /// <summary>
    /// Determines whether an existing index filter already constrains the soft-delete column, so the
    /// convention can append its clause to a hand-authored filter without ever appending it twice.
    /// </summary>
    /// <param name="existingFilter">The filter already declared on the index.</param>
    /// <param name="entityType">The entity type owning the index.</param>
    /// <returns><see langword="true"/> when the filter already carries the soft-delete predicate.</returns>
    /// <remarks>
    /// The comparison is made on a normalized form (whitespace and identifier quoting removed), so a
    /// literal <c>[IsDeleted] = 0</c>, a <c>"IsDeleted"=0</c>, the PostgreSQL
    /// <c>"IsDeleted" = false</c> and the predicate this class produces all count as the same
    /// clause. That matters because the two ways in (a hand-authored <c>HasFilter</c> literal and
    /// <c>HasSoftDeleteFilter</c>) do not agree on quoting, and because the boolean and integer
    /// spellings of the same predicate must not be appended to one another.
    /// </remarks>
    internal static bool ContainsPredicate(string existingFilter, IReadOnlyEntityType entityType)
    {
        var normalized = Normalize(existingFilter);
        var column = ColumnName(entityType);

        return normalized.Contains(Normalize($"{column} = 0"), StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(Normalize($"{column} = false"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Quotes one column name the way <paramref name="engine"/> expects inside a filtered-index
    /// predicate (<see cref="IDataSourceEngine.QuoteColumn"/>): brackets on SQL Server, the
    /// SQL-standard double-quoted form on PostgreSQL (which rejects brackets) and SQLite.
    /// </summary>
    /// <param name="engine">The engine of the model being built.</param>
    /// <param name="column">The column name to quote.</param>
    /// <returns>The quoted identifier.</returns>
    internal static string QuoteColumn(DataSource engine, string column) =>
        DataSourceEngines.For(engine).QuoteColumn(column);

    private static string ColumnName(IReadOnlyEntityType entityType) =>
        entityType.FindProperty(nameof(IAuditableEntity.IsDeleted))?.GetColumnName()
            ?? nameof(IAuditableEntity.IsDeleted);

    /// <summary>Strips whitespace and the three identifier quoting styles the engines use.</summary>
    private static string Normalize(string sql) =>
        string.Concat(sql.Where(c => !char.IsWhiteSpace(c) && c is not ('[' or ']' or '"' or '`')));
}
