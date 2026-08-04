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

        var isDeletedColumn = entityType.FindProperty(nameof(IAuditableEntity.IsDeleted))?.GetColumnName()
            ?? nameof(IAuditableEntity.IsDeleted);

        return engine == DataSource.SQLServer
            ? $"[{isDeletedColumn}] = 0"
            : $"\"{isDeletedColumn}\" = 0";
    }
}
