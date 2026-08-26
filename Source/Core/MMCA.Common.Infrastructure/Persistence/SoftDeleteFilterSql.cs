using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Interfaces;

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
    internal static string? Build(DataSource engine, IReadOnlyEntityType entityType)
    {
        if (engine == DataSource.CosmosDB)
            return null;

        var isDeletedColumn = ColumnName(entityType);

        return engine == DataSource.SQLServer
            ? $"[{isDeletedColumn}] = 0"
            : $"\"{isDeletedColumn}\" = 0";
    }

    /// <summary>
    /// Determines whether an existing index filter already constrains the soft-delete column, so the
    /// convention can append its clause to a hand-authored filter without ever appending it twice.
    /// </summary>
    /// <param name="existingFilter">The filter already declared on the index.</param>
    /// <param name="entityType">The entity type owning the index.</param>
    /// <returns><see langword="true"/> when the filter already carries the soft-delete predicate.</returns>
    /// <remarks>
    /// The comparison is made on a normalized form (whitespace and identifier quoting removed), so a
    /// literal <c>[IsDeleted] = 0</c>, a <c>"IsDeleted"=0</c> and the predicate this class produces
    /// all count as the same clause. That matters because the two ways in (a hand-authored
    /// <c>HasFilter</c> literal and <c>HasSoftDeleteFilter</c>) do not agree on quoting.
    /// </remarks>
    internal static bool ContainsPredicate(string existingFilter, IReadOnlyEntityType entityType) =>
        Normalize(existingFilter).Contains(
            Normalize($"{ColumnName(entityType)} = 0"),
            StringComparison.OrdinalIgnoreCase);

    private static string ColumnName(IReadOnlyEntityType entityType) =>
        entityType.FindProperty(nameof(IAuditableEntity.IsDeleted))?.GetColumnName()
            ?? nameof(IAuditableEntity.IsDeleted);

    /// <summary>Strips whitespace and the three identifier quoting styles the engines use.</summary>
    private static string Normalize(string sql) =>
        string.Concat(sql.Where(c => !char.IsWhiteSpace(c) && c is not ('[' or ']' or '"' or '`')));
}
