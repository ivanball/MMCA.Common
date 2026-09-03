using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Persistence.Configuration;

/// <summary>
/// Extension members for <see cref="IndexBuilder"/> used from entity type configurations.
/// </summary>
public static class IndexBuilderExtensions
{
    extension(IndexBuilder indexBuilder)
    {
        /// <summary>
        /// Filters the index to non-deleted rows. Soft-deleted rows are invisible to the
        /// application (the global query filter hides them) but they still occupy index pages and
        /// still count towards a unique constraint, so an index that serves a live-row query wants
        /// the same <c>IsDeleted = 0</c> predicate the query carries.
        /// <para>
        /// <see cref="Conventions.SoftDeleteUniqueIndexConvention"/> already applies this predicate
        /// to every UNIQUE index on a soft-deletable entity. This extension point is for the other
        /// case, a hand-authored NON-unique index, which the convention deliberately leaves alone:
        /// <code>
        /// builder.HasIndex(p => new { p.CustomerId, p.CreatedOn })
        ///     .HasSoftDeleteFilter();
        /// </code>
        /// It replaces the literal <c>HasFilter("[IsDeleted] = 0")</c>: the column name is read from
        /// the model (so a renamed column follows automatically) and the identifier quoting comes
        /// from the engine rather than from a SQL-Server-shaped string literal.
        /// </para>
        /// <para>
        /// <b>Ordering:</b> unlike the convention, which runs at model finalizing, this reads the
        /// column name when it is called, so a <c>HasColumnName</c> on the soft-delete property must
        /// come first. That only matters for a model that renames the column.
        /// </para>
        /// </summary>
        /// <param name="engine">
        /// The engine of the model being built. Defaults to <see cref="DataSource.SQLServer"/>,
        /// matching <c>EntityTypeConfigurationSQLServer</c>; pass <see cref="DataSource.Sqlite"/>
        /// from a SQLite configuration. For <see cref="DataSource.CosmosDB"/> the call is a no-op,
        /// exactly as the convention skips Cosmos.
        /// </param>
        /// <param name="additionalFilter">
        /// Optional predicate to combine with the soft-delete predicate using <c>AND</c>, for an
        /// index that is also filtered on something else (for example
        /// <c>"[StripeSessionId] IS NOT NULL"</c>). The two are joined in that order, so the
        /// produced SQL matches the hand-authored literal it replaces.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        public IndexBuilder HasSoftDeleteFilter(
            DataSource engine = DataSource.SQLServer,
            string? additionalFilter = null)
        {
            ArgumentNullException.ThrowIfNull(indexBuilder);

            var filterSql = SoftDeleteFilterSql.Build(engine, indexBuilder.Metadata.DeclaringEntityType);
            if (filterSql is null)
                return indexBuilder;

            return indexBuilder.HasFilter(
                string.IsNullOrWhiteSpace(additionalFilter)
                    ? filterSql
                    : $"{additionalFilter} AND {filterSql}");
        }
    }
}
