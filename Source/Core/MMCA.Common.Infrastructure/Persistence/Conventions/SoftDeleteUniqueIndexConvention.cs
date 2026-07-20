using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using MMCA.Common.Application.Interfaces.Infrastructure;
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
/// Hand-authored filters win: an index that already declares a filter is left untouched. The
/// convention covers SQL Server and SQLite (both support partial/filtered indexes); it is a
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
        var isDeletedColumn = entityType.FindProperty(nameof(IAuditableEntity.IsDeleted))?.GetColumnName()
            ?? nameof(IAuditableEntity.IsDeleted);

        var filterSql = engine == DataSource.SQLServer
            ? $"[{isDeletedColumn}] = 0"
            : $"\"{isDeletedColumn}\" = 0";

        foreach (var index in entityType.GetIndexes())
        {
            if (index.IsUnique && index.GetFilter() is null)
                index.SetFilter(filterSql);
        }
    }
}
