using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Persistence.Conventions;

/// <summary>
/// Model-finalizing convention that adds an <c>IsDeleted = 0</c> filter to every unique index on
/// a soft-deletable (<see cref="IAuditableEntity"/>) entity type. Without the filter, a
/// soft-deleted row keeps occupying its unique slot forever: "delete" a speaker and the email's
/// unique index still blocks creating a new speaker with that email, which contradicts what
/// soft-delete presents to users (the global query filter makes the row invisible, but the
/// database still enforces uniqueness against it).
/// <para>
/// A hand-authored filter is kept and EXTENDED rather than replaced or skipped: the soft-delete
/// clause is appended with <c>AND</c>, so an index that is already narrowed on something else
/// (for example <c>[DedupKey] IS NOT NULL</c>) keeps its own predicate and still stops enforcing
/// uniqueness against soft-deleted rows. Skipping such an index, as this convention originally
/// did, silently left the exact partial-unique indexes a model bothered to hand-author as the
/// only ones a soft-deleted row could keep blocking. Appending is idempotent: a filter that
/// already constrains the soft-delete column (a literal <c>[IsDeleted] = 0</c>, or the output of
/// <c>HasSoftDeleteFilter</c>) is left exactly as it is.
/// </para>
/// <para>
/// The convention covers SQL Server and SQLite (both support partial/filtered indexes); it is a
/// no-op for Cosmos.
/// </para>
/// </summary>
/// <param name="engine">The engine of the context whose model is being built (filter syntax differs per provider).</param>
public sealed class SoftDeleteUniqueIndexConvention(DataSource engine) : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        if (engine == DataSource.CosmosDB)
            return;

        var softDeletableTypes = modelBuilder.Metadata.GetEntityTypes()
            .Where(et => typeof(IAuditableEntity).IsAssignableFrom(et.ClrType) && !et.IsOwned());

        foreach (var entityType in softDeletableTypes)
            ApplyFilterToUniqueIndexes(entityType);
    }

    private void ApplyFilterToUniqueIndexes(IConventionEntityType entityType)
    {
        // Same predicate builder a hand-authored index reaches through HasSoftDeleteFilter(), so the
        // automatic and the opt-in path can never disagree about quoting or column name.
        var filterSql = SoftDeleteFilterSql.Build(engine, entityType);
        if (filterSql is null)
            return;

        foreach (var index in entityType.GetIndexes())
        {
            if (!index.IsUnique)
                continue;

            var existingFilter = index.GetFilter();
            if (string.IsNullOrWhiteSpace(existingFilter))
            {
                index.SetFilter(filterSql);
                continue;
            }

            // Already constrained on IsDeleted (hand-authored literal or HasSoftDeleteFilter): leave
            // it alone, or a second model build would produce "... AND [IsDeleted] = 0 AND [IsDeleted] = 0".
            if (SoftDeleteFilterSql.ContainsPredicate(existingFilter, entityType))
                continue;

            // Same order HasSoftDeleteFilter(additionalFilter:) produces, so the two paths yield
            // byte-identical SQL for the same pair of predicates.
            index.SetFilter($"{existingFilter} AND {filterSql}");
        }
    }
}
